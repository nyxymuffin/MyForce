using MQTTnet;
using MQTTnet.Formatter;
using Config.Net;

using var instanceLock = SingleInstanceLock.TryAcquire("myforce-audio-processor");
if (instanceLock is null)
{
    AudioProcessorLog.Write("startup", "Another Audio Processor instance is already running. Exiting to avoid MQTT client id takeover.");
    return;
}

await using var processor = new AudioProcessorMqttApp();
await processor.RunAsync();

internal static class AudioProcessorLog
{
    public static void Write(string category, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Console.WriteLine($"[{DateTimeOffset.UtcNow:O}] [audio-processor] [{category}] {message}");
    }
}

internal sealed class AudioProcessorMqttApp : IAsyncDisposable
{
    private readonly MqttServiceRuntime _mqttRuntime;
    private readonly AudioProcessorCoordinator _coordinator;
    private readonly PeriodicTimer _statusHeartbeatTimer = new(TimeSpan.FromSeconds(5));
    private Task? _statusHeartbeatTask;

    public AudioProcessorMqttApp()
    {
        var topics = new AudioProcessorTopicFactory();
        var lastWillPayload = AudioProcessorJson.Serialize(
            ServiceStatusPayload.CreateStopped(
                serviceId: "audio-processor",
                radioCount: 0,
                bridgeCount: 0,
                activeManualTransmitRadioId: null));

        _mqttRuntime = new MqttServiceRuntime(
            serviceName: "audio-processor",
            lastWillMessage: new MqttLastWillMessage(topics.ServiceStatusTopic, lastWillPayload, true));
        _coordinator = new AudioProcessorCoordinator(_mqttRuntime, topics);
        _mqttRuntime.SetConnectedHandler(_coordinator.HandleConnectedAsync);
        _mqttRuntime.SetMessageHandler(_coordinator.HandleMessageAsync);
    }

    public async Task RunAsync()
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;

        try
        {
            await _mqttRuntime.ConnectAsync(cts.Token);
            await _coordinator.StartAsync(cts.Token);
            _statusHeartbeatTask = RunStatusHeartbeatAsync(cts.Token);
            AudioProcessorLog.Write("startup", "Audio Processor basics ready.");
            await _mqttRuntime.RunUntilStoppedAsync(cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _statusHeartbeatTimer.Dispose();

        if (_statusHeartbeatTask is not null)
        {
            try
            {
                await _statusHeartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _coordinator.DisposeAsync();
        await _mqttRuntime.DisposeAsync();
    }

    private async Task RunStatusHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _statusHeartbeatTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _coordinator.PublishHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

internal sealed class MqttServiceRuntime : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly Lock _syncRoot = new();
    private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();
    private readonly MqttLastWillMessage? _lastWillMessage;
    private readonly MqttServiceOptions _options;
    private readonly string _serviceName;
    private MqttClientOptions? _clientOptions;
    private Task? _reconnectTask;
    private bool _isDisposed;
    private bool _isReconnectLoopRunning;
    private Func<CancellationToken, Task>? _connectedHandler;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _messageHandler;

    public MqttServiceRuntime(string serviceName, MqttLastWillMessage? lastWillMessage = null)
    {
        _serviceName = serviceName;
        _lastWillMessage = lastWillMessage;
        _options = MqttServiceOptions.Load(serviceName);
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ConnectedAsync += OnConnectedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        AudioProcessorLog.Write("mqtt", $"MQTT configured for {GetEndpointLabel()} using {GetTransportLabel()}.");

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithClientId(_options.ClientId)
            .WithTcpServer(_options.Host, _options.Port)
            .WithCleanSession();

        if (_lastWillMessage is not null)
        {
            optionsBuilder = optionsBuilder
                .WithWillTopic(_lastWillMessage.Topic)
                .WithWillPayload(_lastWillMessage.Payload)
                .WithWillRetain(_lastWillMessage.Retain);
        }

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            optionsBuilder = optionsBuilder.WithCredentials(_options.Username, _options.Password);
        }

        if (_options.UseTls)
        {
            optionsBuilder = optionsBuilder.WithTlsOptions(static builder => builder.UseTls());
        }

        _clientOptions = optionsBuilder.Build();
        await TryConnectAsync(cancellationToken).ConfigureAwait(false);
        StartReconnectLoop();
    }

    public void SetMessageHandler(Func<MqttApplicationMessageReceivedEventArgs, Task> messageHandler)
    {
        ArgumentNullException.ThrowIfNull(messageHandler);
        _messageHandler = messageHandler;
    }

    public void SetConnectedHandler(Func<CancellationToken, Task> connectedHandler)
    {
        ArgumentNullException.ThrowIfNull(connectedHandler);
        _connectedHandler = connectedHandler;
    }

    public async Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicFilter);
        if (!_client.IsConnected)
        {
            AudioProcessorLog.Write("mqtt", $"Subscribe skipped while MQTT is offline: {topicFilter}");
            return;
        }

        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topicFilter)
            .Build();

        try
        {
            await _client.SubscribeAsync(subscribeOptions, cancellationToken).ConfigureAwait(false);
            AudioProcessorLog.Write("mqtt", $"Subscribed: {topicFilter}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AudioProcessorLog.Write("mqtt", $"Subscribe failed for {topicFilter}: {ex.Message}");
        }
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (!_client.IsConnected)
        {
            AudioProcessorLog.Write("mqtt", $"Publish skipped while MQTT is offline: {topic}");
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();

        try
        {
            await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            AudioProcessorLog.Write("mqtt", $"Published: {topic}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AudioProcessorLog.Write("mqtt", $"Publish failed for {topic}: {ex.Message}");
        }
    }

    public async Task RunUntilStoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? reconnectTask;

        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            reconnectTask = _reconnectTask;
        }

        await _lifetimeCancellationTokenSource.CancelAsync().ConfigureAwait(false);
        _client.ConnectedAsync -= OnConnectedAsync;
        _client.DisconnectedAsync -= OnDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;

        if (reconnectTask is not null)
        {
            try
            {
                await reconnectTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync().ConfigureAwait(false);
        }

        _client.Dispose();
        _lifetimeCancellationTokenSource.Dispose();
    }

    private async Task OnConnectedAsync(MqttClientConnectedEventArgs arg)
    {
        AudioProcessorLog.Write("mqtt", $"Connected to MQTT broker at {GetEndpointLabel()} using {GetTransportLabel()} with client id '{_options.ClientId}'.");

        if (_connectedHandler is null)
        {
            return;
        }

        try
        {
            await _connectedHandler(_lifetimeCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AudioProcessorLog.Write("mqtt", $"MQTT connected handler failed: {ex.Message}");
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        var detail = DescribeDisconnect(arg.Exception?.Message ?? arg.ReasonString ?? "Disconnected.");
        AudioProcessorLog.Write("mqtt", $"MQTT disconnected from {GetEndpointLabel()}: {detail}");
        StartReconnectLoop();
        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        AudioProcessorLog.Write("mqtt", $"Received message on topic: {arg.ApplicationMessage.Topic}");

        if (_messageHandler is null)
        {
            return;
        }

        try
        {
            await _messageHandler(arg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AudioProcessorLog.Write("mqtt", $"MQTT message handler failed: {ex.Message}");
        }
    }

    private async Task TryConnectAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_clientOptions);

        try
        {
            await _client.ConnectAsync(_clientOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AudioProcessorLog.Write("mqtt", $"Initial MQTT connect failed for {GetEndpointLabel()} using {GetTransportLabel()} with client id '{_options.ClientId}': {ex.Message}");
        }
    }

    private void StartReconnectLoop()
    {
        lock (_syncRoot)
        {
            if (_isDisposed || _isReconnectLoopRunning || _client.IsConnected || _clientOptions is null)
            {
                return;
            }

            _isReconnectLoopRunning = true;
            _reconnectTask = RunReconnectLoopAsync();
        }
    }

    private async Task RunReconnectLoopAsync()
    {
        try
        {
            while (!_lifetimeCancellationTokenSource.IsCancellationRequested)
            {
                if (_client.IsConnected)
                {
                    return;
                }

                try
                {
                    ArgumentNullException.ThrowIfNull(_clientOptions);
                    AudioProcessorLog.Write("mqtt", $"Attempting MQTT reconnect to {GetEndpointLabel()} using {GetTransportLabel()} with client id '{_options.ClientId}'.");
                    await _client.ConnectAsync(_clientOptions, _lifetimeCancellationTokenSource.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (_lifetimeCancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AudioProcessorLog.Write("mqtt", $"MQTT reconnect failed for {GetEndpointLabel()} using {GetTransportLabel()} with client id '{_options.ClientId}': {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), _lifetimeCancellationTokenSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellationTokenSource.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_syncRoot)
            {
                _isReconnectLoopRunning = false;
                _reconnectTask = null;
            }

            if (!_isDisposed && !_client.IsConnected)
            {
                StartReconnectLoop();
            }
        }
    }

    private string GetEndpointLabel()
    {
        return $"{_options.Host}:{_options.Port}";
    }

    private string GetTransportLabel()
    {
        return _options.UseTls ? "encrypted MQTT" : "unencrypted MQTT";
    }

    private string DescribeDisconnect(string detail)
    {
        if (detail.Contains("SessionTakenOver", StringComparison.OrdinalIgnoreCase))
        {
            return $"{detail} Another connection is using MQTT client id '{_options.ClientId}'. Ensure only one Audio Processor instance is running.";
        }

        return detail;
    }
}

internal sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstanceLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static SingleInstanceLock? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var mutex = new Mutex(initiallyOwned: false, $"Global\\{name}");

        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero, exitContext: false))
            {
                mutex.Dispose();
                return null;
            }

            return new SingleInstanceLock(mutex);
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceLock(mutex);
        }
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

internal sealed record MqttLastWillMessage(string Topic, string Payload, bool Retain);

internal sealed record MqttServiceOptions(
    string Host,
    int Port,
    string ClientId,
    bool UseTls,
    string? Username,
    string? Password)
{
    public static MqttServiceOptions Load(string serviceName)
    {
        var normalizedServiceName = serviceName.Replace(' ', '-').ToLowerInvariant();
        var configStore = new AudioProcessorConfigStore();
        var clientId = configStore.StoredConfig.MqttClientId;

        return new MqttServiceOptions(
            Host: string.IsNullOrWhiteSpace(configStore.StoredConfig.MqttHost) ? "127.0.0.1" : configStore.StoredConfig.MqttHost,
            Port: int.TryParse(configStore.StoredConfig.MqttPort, out var port) ? port : 1883,
            ClientId: string.IsNullOrWhiteSpace(clientId) ? $"myforce-{normalizedServiceName}-{Environment.MachineName}" : clientId,
            UseTls: false,
            Username: configStore.StoredConfig.MqttUsername,
            Password: configStore.StoredConfig.MqttPassword);
    }
}

internal sealed class AudioProcessorConfigStore
{
    private const string ConfigFileName = "audio-processor.config.json";
    private const string ConfigDirectoryName = "myforce";

    public AudioProcessorConfigStore()
    {
        var configPath = ResolveConfigPath();
        AudioProcessorLog.Write("config", $"Using config path: {configPath}");
        var configDirectory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }

        StoredConfig = new ConfigurationBuilder<IAudioProcessorStoredConfig>()
            .UseJsonFile(configPath)
            .Build();
    }

    public IAudioProcessorStoredConfig StoredConfig { get; }

    private static string ResolveConfigPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("MYFORCE_AUDIO_PROCESSOR_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var appConfigDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appConfigDirectory))
        {
            return Path.Combine(appConfigDirectory, ConfigDirectoryName, ConfigFileName);
        }

        return Path.Combine(AppContext.BaseDirectory, ConfigFileName);
    }
}

public interface IAudioProcessorStoredConfig
{
    string? MqttHost { get; set; }

    string? MqttPort { get; set; }

    string? MqttClientId { get; set; }

    string? MqttUseTls { get; set; }

    string? MqttUsername { get; set; }

    string? MqttPassword { get; set; }

    string? OutputSpeakerDeviceId { get; set; }

    string? InternetRadioPlayCommandJson { get; set; }
}
