using MQTTnet;
using MQTTnet.Formatter;
using System.Text.Json;

var sirenInterface = new SirenInterfaceMqttApp();
await sirenInterface.RunAsync();

internal sealed class SirenInterfaceMqttApp
{
    private readonly MqttServiceRuntime _mqttRuntime;
    private readonly PeriodicTimer _statusHeartbeatTimer = new(TimeSpan.FromSeconds(5));
    private Task? _statusHeartbeatTask;
    private readonly string _serviceStatusTopic = "myforce/siren/status/service";
    private readonly MqttLastWillMessage _lastWillMessage;

    public SirenInterfaceMqttApp()
    {
        var lastWillPayload = JsonSerializer.Serialize(new SirenServiceStatusPayload("siren-interface", "Stopped", "Heartbeat stopped."));
        _lastWillMessage = new MqttLastWillMessage(_serviceStatusTopic, lastWillPayload, true);
        _mqttRuntime = new MqttServiceRuntime("siren-interface", _lastWillMessage);
        _mqttRuntime.SetConnectedHandler(PublishStatusAsync);
    }

    public async Task RunAsync()
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await _mqttRuntime.ConnectAsync(cts.Token);
        await PublishStatusAsync(cts.Token);
        _statusHeartbeatTask = RunStatusHeartbeatAsync(cts.Token);
        Console.WriteLine("Siren Interface MQTT framework ready.");
        await _mqttRuntime.RunUntilStoppedAsync(cts.Token);
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

        await _mqttRuntime.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the Siren Interface heartbeat payload so the UI can actively monitor service freshness.
    /// </summary>
    private async Task PublishStatusAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new SirenServiceStatusPayload(
            ServiceId: "siren-interface",
            State: "Running",
            Detail: $"Heartbeat: {DateTime.UtcNow:O}"));
        await _mqttRuntime.PublishAsync(_serviceStatusTopic, payload, retain: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunStatusHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _statusHeartbeatTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
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

    public MqttServiceRuntime(string serviceName, MqttLastWillMessage? lastWillMessage = null)
    {
        _serviceName = serviceName;
        _lastWillMessage = lastWillMessage;
        _options = MqttServiceOptions.FromEnvironment(serviceName);
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ConnectedAsync += OnConnectedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

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
            Console.WriteLine($"[{_serviceName}] Subscribe skipped while MQTT is offline: {topicFilter}");
            return;
        }

        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topicFilter)
            .Build();

        try
        {
            await _client.SubscribeAsync(subscribeOptions, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[{_serviceName}] Subscribed: {topicFilter}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_serviceName}] Subscribe failed for {topicFilter}: {ex.Message}");
        }
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        if (!_client.IsConnected)
        {
            Console.WriteLine($"[{_serviceName}] Publish skipped while MQTT is offline: {topic}");
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
            Console.WriteLine($"[{_serviceName}] Published: {topic}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_serviceName}] Publish failed for {topic}: {ex.Message}");
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
        finally
        {
            await DisposeAsync();
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
        Console.WriteLine($"[{_serviceName}] Connected to MQTT broker at {_options.Host}:{_options.Port}.");

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
            Console.WriteLine($"[{_serviceName}] MQTT connected handler failed: {ex.Message}");
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        var detail = arg.Exception?.Message ?? arg.ReasonString ?? "Disconnected.";
        Console.WriteLine($"[{_serviceName}] MQTT disconnected: {detail}");
        StartReconnectLoop();
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        Console.WriteLine($"[{_serviceName}] Received message on topic: {arg.ApplicationMessage.Topic}");
        return Task.CompletedTask;
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
            Console.WriteLine($"[{_serviceName}] Initial MQTT connect failed: {ex.Message}");
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
                    Console.WriteLine($"[{_serviceName}] Attempting MQTT reconnect.");
                    await _client.ConnectAsync(_clientOptions, _lifetimeCancellationTokenSource.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (_lifetimeCancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{_serviceName}] MQTT reconnect failed: {ex.Message}");
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
}

internal sealed record MqttLastWillMessage(string Topic, string Payload, bool Retain);

internal sealed record SirenServiceStatusPayload(string ServiceId, string State, string Detail);

internal sealed record MqttServiceOptions(
    string Host,
    int Port,
    string ClientId,
    bool UseTls,
    string? Username,
    string? Password)
{
    public static MqttServiceOptions FromEnvironment(string serviceName)
    {
        var normalizedServiceName = serviceName.Replace(' ', '-').ToLowerInvariant();
        var clientId = Environment.GetEnvironmentVariable("MYFORCE_MQTT_CLIENT_ID");

        return new MqttServiceOptions(
            Host: Environment.GetEnvironmentVariable("MYFORCE_MQTT_HOST") ?? "127.0.0.1",
            Port: int.TryParse(Environment.GetEnvironmentVariable("MYFORCE_MQTT_PORT"), out var port) ? port : 1883,
            ClientId: string.IsNullOrWhiteSpace(clientId) ? $"myforce-{normalizedServiceName}-{Environment.MachineName}" : clientId,
            UseTls: bool.TryParse(Environment.GetEnvironmentVariable("MYFORCE_MQTT_TLS"), out var useTls) && useTls,
            Username: Environment.GetEnvironmentVariable("MYFORCE_MQTT_USERNAME"),
            Password: Environment.GetEnvironmentVariable("MYFORCE_MQTT_PASSWORD"));
    }
}
