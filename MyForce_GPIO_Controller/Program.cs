// %%%%%%    @%%%%%@
//%%%%%%%%   %%%%%%%@
//@%%%%%%%@  %%%%%%%%%        @@      @@  @@@      @@@ @@@     @@@ @@@@@@@@@@   @@@@@@@@@
//%%%%%%%%@ @%%%%%%%%       @@@@@   @@@@ @@@@@   @@@@ @@@@   @@@@ @@@@@@@@@@@@@@@@@@@@@@@ @@@@
// @%%%%%%%%  %%%%%%%%%      @@@@@@  @@@@  @@@@  @@@@   @@@@@@@@@     @@@@    @@@@         @@@@
//  %%%%%%%%%  %%%%%%%%@     @@@@@@@ @@@@   @@@@@@@@     @@@@@@       @@@@    @@@@@@@@@@@  @@@@
//   %%%%%%%%@  %%%%%%%%%    @@@@@@@@@@@@     @@@@        @@@@@       @@@@    @@@@@@@@@@@  @@@@
//    %%%%%%%%@ @%%%%%%%%    @@@@ @@@@@@@     @@@@      @@@@@@@@      @@@@    @@@@         @@@@
//    @%%%%%%%%% @%%%%%%%%   @@@@   @@@@@     @@@@     @@@@@ @@@@@    @@@@    @@@@@@@@@@@@ @@@@@@@@@@
//     @%%%%%%%%  %%%%%%%%@  @@@@    @@@@     @@@@    @@@@     @@@@   @@@@    @@@@@@@@@@@@ @@@@@@@@@@@
//      %%%%%%%%@ @%%%%%%%%
//      @%%%%%%%%  @%%%%%%%%
//       %%%%%%%%   %%%%%%%@
//         %%%%%      %%%%
//
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
using MQTTnet;
using MQTTnet.Formatter;
using SerialPortLib;
using System.Buffers;
using System.Globalization;
using System.Text.Json;

using Config.Net;

using var instanceLock = SingleInstanceLock.TryAcquire("myforce-gpio-controller");
if (instanceLock is null)
{
	Console.WriteLine("[gpio-controller] Another GPIO Controller instance is already running. Exiting to avoid MQTT client id takeover.");
	return;
}

await using var controller = new GpioControllerMqttApp();
await controller.RunAsync();

internal sealed class GpioControllerMqttApp : IAsyncDisposable
{
	private readonly MqttServiceRuntime _mqttRuntime;

	private readonly GpioControllerCoordinator _coordinator;

	public GpioControllerMqttApp()
	{
		var topics = new GpioControllerTopicFactory();
		var lastWillPayload = GpioControllerJson.Serialize(
			GpioControllerServiceStatusPayload.CreateStopped(
				serviceId: "gpio-controller",
				relayBoardCount: 0,
				digitalInputBoardCount: 0,
				detail: "GPIO controller stopped."));

		_mqttRuntime = new MqttServiceRuntime(
			serviceName: "gpio-controller",
			lastWillMessage: new MqttLastWillMessage(topics.ServiceStatusTopic, lastWillPayload, true));
		_coordinator = new GpioControllerCoordinator(_mqttRuntime, topics);
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
			Console.WriteLine("GPIO Controller MQTT ready.");
			await _mqttRuntime.RunUntilStoppedAsync(cts.Token);
		}
		finally
		{
			Console.CancelKeyPress -= cancelHandler;
		}
	}

	public async ValueTask DisposeAsync()
	{
		await _coordinator.DisposeAsync();
		await _mqttRuntime.DisposeAsync();
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

		Console.WriteLine($"[{_serviceName}] MQTT configured for {GetEndpointLabel()} using {GetTransportLabel()}.");

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

	public void SetMessageHandler(Func<MqttApplicationMessageReceivedEventArgs, Task> messageHandler)
	{
		ArgumentNullException.ThrowIfNull(messageHandler);
		_messageHandler = messageHandler;
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
		Console.WriteLine($"[{_serviceName}] Connected to MQTT broker at {GetEndpointLabel()} using {GetTransportLabel()} with client id '{_options.ClientId}'.");

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
		var detail = DescribeDisconnect(arg.Exception?.Message ?? arg.ReasonString ?? "Disconnected.");
		Console.WriteLine($"[{_serviceName}] MQTT disconnected from {GetEndpointLabel()}: {detail}");
		StartReconnectLoop();
		return Task.CompletedTask;
	}

	private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
	{
		Console.WriteLine($"[{_serviceName}] Received message on topic: {arg.ApplicationMessage.Topic}");

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
			Console.WriteLine($"[{_serviceName}] MQTT message handler failed: {ex.Message}");
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
			Console.WriteLine($"[{_serviceName}] Initial MQTT connect failed for {GetEndpointLabel()} using {GetTransportLabel()} with client id '{_options.ClientId}': {ex.Message}");
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
					Console.WriteLine($"[{_serviceName}] Attempting MQTT reconnect to {GetEndpointLabel()} using {GetTransportLabel()} with client id '{_options.ClientId}'.");
					await _client.ConnectAsync(_clientOptions, _lifetimeCancellationTokenSource.Token).ConfigureAwait(false);
					return;
				}
				catch (OperationCanceledException) when (_lifetimeCancellationTokenSource.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[{_serviceName}] MQTT reconnect failed for {GetEndpointLabel()} using {GetTransportLabel()} with client id '{_options.ClientId}': {ex.Message}");
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
			return $"{detail} Another connection is using MQTT client id '{_options.ClientId}'. Ensure only one GPIO Controller instance is running.";
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

internal sealed class GpioControllerCoordinator : IAsyncDisposable
{
	private readonly MqttServiceRuntime _mqttRuntime;

	private readonly GpioBoardSerialConnectionManager _boardSerialConnectionManager;

	private readonly GpioControllerTopicFactory _topics;

	private readonly GpioControllerConfigStore _configStore;

	private GpioControllerConfigPayload _currentConfig;

	private bool _hasPublishedConnectedSnapshot;

	private string _startupStatusDetail;

	public GpioControllerCoordinator(MqttServiceRuntime mqttRuntime, GpioControllerTopicFactory topics)
	{
		ArgumentNullException.ThrowIfNull(mqttRuntime);
		ArgumentNullException.ThrowIfNull(topics);

		_mqttRuntime = mqttRuntime;
		_boardSerialConnectionManager = new GpioBoardSerialConnectionManager();
		_topics = topics;
		_configStore = new GpioControllerConfigStore();
		_currentConfig = GpioControllerConfigPayload.Empty;
		_startupStatusDetail = "GPIO controller ready for UI config over MQTT.";
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_startupStatusDetail = LoadPersistedConfig();
		return Task.CompletedTask;
	}

	public async Task HandleConnectedAsync(CancellationToken cancellationToken)
	{
		await _mqttRuntime.SubscribeAsync(_topics.AllCommandsTopicFilter, cancellationToken).ConfigureAwait(false);
		await _mqttRuntime.SubscribeAsync(_topics.LegacyAllCommandsTopicFilter, cancellationToken).ConfigureAwait(false);
		var detail = _hasPublishedConnectedSnapshot
			   ? "GPIO controller reconnected to MQTT broker."
			   : _startupStatusDetail;

		await PublishBirthSnapshotAsync(detail, cancellationToken).ConfigureAwait(false);
		_hasPublishedConnectedSnapshot = true;
	}

	public async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args)
	{
		ArgumentNullException.ThrowIfNull(args);

		var topic = args.ApplicationMessage.Topic;
		if (string.IsNullOrWhiteSpace(topic))
		{
			return;
		}

		if (string.Equals(topic, _topics.ApplyConfigTopic, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(topic, _topics.LegacyApplyConfigTopic, StringComparison.OrdinalIgnoreCase))
		{
			await ApplyConfigAsync(args.ApplicationMessage.Payload, CancellationToken.None).ConfigureAwait(false);
			return;
		}

		if (string.Equals(topic, _topics.ClearConfigTopic, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(topic, _topics.LegacyClearConfigTopic, StringComparison.OrdinalIgnoreCase))
		{
			_currentConfig = GpioControllerConfigPayload.Empty;
			_configStore.Clear();
			await PublishConfigSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishStatusAsync("GPIO controller config cleared.", CancellationToken.None).ConfigureAwait(false);
		}
	}

	public ValueTask DisposeAsync()
	{
		_boardSerialConnectionManager.Dispose();
		return ValueTask.CompletedTask;
	}

	private async Task ApplyConfigAsync(ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
	{
		try
		{
			var config = GpioControllerJson.Deserialize<GpioControllerConfigPayload>(payload);
			if (config is null)
			{
				await PublishStatusAsync("GPIO controller config payload was empty or invalid JSON.", cancellationToken).ConfigureAwait(false);
				return;
			}

			ValidateConfig(config);
			_currentConfig = config;
			_configStore.Save(config);
			_boardSerialConnectionManager.ApplyConfig(_currentConfig);
			await PublishConfigSnapshotAsync(cancellationToken).ConfigureAwait(false);
			await PublishStatusAsync("GPIO controller config applied.", cancellationToken).ConfigureAwait(false);
		}
		catch (JsonException ex)
		{
			await PublishStatusAsync($"GPIO controller config JSON error: {ex.Message}", cancellationToken).ConfigureAwait(false);
		}
		catch (ArgumentException ex)
		{
			await PublishStatusAsync($"GPIO controller config rejected: {ex.Message}", cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task PublishBirthSnapshotAsync(string detail, CancellationToken cancellationToken)
	{
		await _mqttRuntime.PublishAsync(
			_topics.ServiceRegistryTopic,
			GpioControllerJson.Serialize(GpioControllerServiceRegistryPayload.Create(_topics)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		await _mqttRuntime.PublishAsync(
			_topics.LegacyServiceRegistryTopic,
			GpioControllerJson.Serialize(GpioControllerServiceRegistryPayload.Create(_topics)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		await PublishConfigSnapshotAsync(cancellationToken).ConfigureAwait(false);
		await PublishStatusAsync(detail, cancellationToken).ConfigureAwait(false);
	}

	private string LoadPersistedConfig()
	{
		try
		{
			var persistedConfig = _configStore.Load();
			ValidateConfig(persistedConfig);
			_currentConfig = persistedConfig;
			_boardSerialConnectionManager.ApplyConfig(_currentConfig);
			return _currentConfig == GpioControllerConfigPayload.Empty
				? "GPIO controller ready for UI config over MQTT."
				: "GPIO controller loaded persisted config."
				;
		}
		catch (JsonException ex)
		{
			_currentConfig = GpioControllerConfigPayload.Empty;
			return $"GPIO controller persisted config JSON error: {ex.Message}";
		}
		catch (ArgumentException ex)
		{
			_currentConfig = GpioControllerConfigPayload.Empty;
			return $"GPIO controller persisted config rejected: {ex.Message}";
		}
	}

	private async Task PublishConfigSnapshotAsync(CancellationToken cancellationToken)
	{
		await _mqttRuntime.PublishAsync(
			_topics.ConfigStateTopic,
			GpioControllerJson.Serialize(_currentConfig),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		await _mqttRuntime.PublishAsync(
			_topics.LegacyConfigStateTopic,
			GpioControllerJson.Serialize(_currentConfig),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private async Task PublishStatusAsync(string detail, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(detail);

		await _mqttRuntime.PublishAsync(
			_topics.ServiceStatusTopic,
			GpioControllerJson.Serialize(
				GpioControllerServiceStatusPayload.CreateRunning(
					serviceId: "gpio-controller",
					relayBoardCount: _currentConfig.RelayBoards.Count,
					digitalInputBoardCount: _currentConfig.DigitalInputBoards.Count,
					detail: detail)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		await _mqttRuntime.PublishAsync(
			_topics.LegacyServiceStatusTopic,
			GpioControllerJson.Serialize(
				GpioControllerServiceStatusPayload.CreateRunning(
					serviceId: "gpio-controller",
					relayBoardCount: _currentConfig.RelayBoards.Count,
					digitalInputBoardCount: _currentConfig.DigitalInputBoards.Count,
					detail: detail)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private static void ValidateConfig(GpioControllerConfigPayload config)
	{
		ArgumentNullException.ThrowIfNull(config);

		ValidateRelayBoards(config.RelayBoards);
		ValidateDigitalInputBoards(config.DigitalInputBoards);
	}

	private static void ValidateRelayBoards(IReadOnlyList<RelayBoardConfigPayload> relayBoards)
	{
		ArgumentNullException.ThrowIfNull(relayBoards);

		var boardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var relayBoard in relayBoards)
		{
			ArgumentNullException.ThrowIfNull(relayBoard);
			if (!boardIds.Add(relayBoard.RelayBoardId))
			{
				throw new ArgumentException($"Duplicate relay board id '{relayBoard.RelayBoardId}'.");
			}

			ValidateBoardCore(
				boardId: relayBoard.RelayBoardId,
				boardType: relayBoard.RelayBoardType,
				channelCount: relayBoard.RelayBoardNumberOfChannels,
				comPort: relayBoard.RelayBoardComPort,
				baud: relayBoard.RelayBoardBaud);

			ValidateRelayBoardChannels(relayBoard);
		}
	}

	private static void ValidateRelayBoardChannels(RelayBoardConfigPayload relayBoard)
	{
		var channelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var channel in relayBoard.RelayBoardChannel)
		{
			ArgumentNullException.ThrowIfNull(channel);
			if (string.IsNullOrWhiteSpace(channel.RelayBoardChannelId))
			{
				throw new ArgumentException($"Relay board '{relayBoard.RelayBoardId}' has an empty channel id.");
			}

			if (!channelIds.Add(channel.RelayBoardChannelId))
			{
				throw new ArgumentException($"Relay board '{relayBoard.RelayBoardId}' has duplicate channel id '{channel.RelayBoardChannelId}'.");
			}

			if (string.IsNullOrWhiteSpace(channel.RelayBoardChannelFunction))
			{
				throw new ArgumentException($"Relay board '{relayBoard.RelayBoardId}' channel '{channel.RelayBoardChannelId}' is missing a function.");
			}
		}

		if (relayBoard.RelayBoardChannel.Count > relayBoard.RelayBoardNumberOfChannels)
		{
			throw new ArgumentException($"Relay board '{relayBoard.RelayBoardId}' defines more channels than RelayBoardNumberOfChannels.");
		}
	}

	private static void ValidateDigitalInputBoards(IReadOnlyList<DigitalInputBoardConfigPayload> digitalInputBoards)
	{
		ArgumentNullException.ThrowIfNull(digitalInputBoards);

		var boardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var digitalInputBoard in digitalInputBoards)
		{
			ArgumentNullException.ThrowIfNull(digitalInputBoard);
			if (!boardIds.Add(digitalInputBoard.DigitalInputBoardId))
			{
				throw new ArgumentException($"Duplicate digital input board id '{digitalInputBoard.DigitalInputBoardId}'.");
			}

			ValidateBoardCore(
				boardId: digitalInputBoard.DigitalInputBoardId,
				boardType: digitalInputBoard.DigitalInputBoardType,
				channelCount: digitalInputBoard.DigitalInputBoardNumberOfChannels,
				comPort: digitalInputBoard.DigitalInputBoardComPort,
				baud: digitalInputBoard.DigitalInputBoardBaud);

			ValidateDigitalInputBoardChannels(digitalInputBoard);
		}
	}

	private static void ValidateDigitalInputBoardChannels(DigitalInputBoardConfigPayload digitalInputBoard)
	{
		var channelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var channel in digitalInputBoard.DigitalInputBoardChannel)
		{
			ArgumentNullException.ThrowIfNull(channel);
			if (string.IsNullOrWhiteSpace(channel.DigitalInputBoardChannelId))
			{
				throw new ArgumentException($"Digital input board '{digitalInputBoard.DigitalInputBoardId}' has an empty channel id.");
			}

			if (!channelIds.Add(channel.DigitalInputBoardChannelId))
			{
				throw new ArgumentException($"Digital input board '{digitalInputBoard.DigitalInputBoardId}' has duplicate channel id '{channel.DigitalInputBoardChannelId}'.");
			}

			if (string.IsNullOrWhiteSpace(channel.DigitalInputBoardChannelFunction))
			{
				throw new ArgumentException($"Digital input board '{digitalInputBoard.DigitalInputBoardId}' channel '{channel.DigitalInputBoardChannelId}' is missing a function.");
			}
		}

		if (digitalInputBoard.DigitalInputBoardChannel.Count > digitalInputBoard.DigitalInputBoardNumberOfChannels)
		{
			throw new ArgumentException($"Digital input board '{digitalInputBoard.DigitalInputBoardId}' defines more channels than DigitalInputBoardNumberOfChannels.");
		}
	}

	private static void ValidateBoardCore(string boardId, string boardType, int channelCount, string comPort, int baud)
	{
		if (string.IsNullOrWhiteSpace(boardId))
		{
			throw new ArgumentException("Board id is required.");
		}

		if (string.IsNullOrWhiteSpace(boardType))
		{
			throw new ArgumentException($"Board '{boardId}' is missing a board type.");
		}

		if (!GpioBoardType.IsSupported(boardType))
		{
			throw new ArgumentException($"Board '{boardId}' has unsupported board type '{boardType}'.");
		}

		if (channelCount < 1)
		{
			throw new ArgumentException($"Board '{boardId}' must define at least one channel.");
		}

		if (string.IsNullOrWhiteSpace(comPort))
		{
			throw new ArgumentException($"Board '{boardId}' is missing a COM port.");
		}

		if (baud < 1)
		{
			throw new ArgumentException($"Board '{boardId}' must define a baud rate greater than zero.");
		}
	}
}

internal sealed class GpioBoardSerialConnectionManager : IDisposable
{
	private readonly Dictionary<string, GpioBoardSerialConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

	public void ApplyConfig(GpioControllerConfigPayload config)
	{
		ArgumentNullException.ThrowIfNull(config);

		var desiredConnections = CreateDesiredConnections(config);
		var desiredKeys = new HashSet<string>(desiredConnections.Keys, StringComparer.OrdinalIgnoreCase);

		foreach (var existingConnection in _connections.Keys.Except(desiredKeys, StringComparer.OrdinalIgnoreCase).ToArray())
		{
			RemoveConnection(existingConnection);
		}

		foreach (var desiredConnection in desiredConnections)
		{
			if (_connections.TryGetValue(desiredConnection.Key, out var existingConnection))
			{
				if (existingConnection.Definition == desiredConnection.Value)
				{
					continue;
				}

				RemoveConnection(desiredConnection.Key);
			}

			_connections[desiredConnection.Key] = OpenConnection(desiredConnection.Value);
		}
	}

	public void Dispose()
	{
		foreach (var connectionKey in _connections.Keys.ToArray())
		{
			RemoveConnection(connectionKey);
		}
	}

	private static Dictionary<string, GpioBoardSerialConnectionDefinition> CreateDesiredConnections(GpioControllerConfigPayload config)
	{
		var desiredConnections = new Dictionary<string, GpioBoardSerialConnectionDefinition>(StringComparer.OrdinalIgnoreCase);

		foreach (var relayBoard in config.RelayBoards)
		{
			var definition = new GpioBoardSerialConnectionDefinition(
				BoardCategory: GpioBoardCategory.RelayBoard,
				BoardId: relayBoard.RelayBoardId,
				PortName: relayBoard.RelayBoardComPort,
				BaudRate: relayBoard.RelayBoardBaud);

			desiredConnections[definition.ConnectionKey] = definition;
		}

		foreach (var digitalInputBoard in config.DigitalInputBoards)
		{
			var definition = new GpioBoardSerialConnectionDefinition(
				BoardCategory: GpioBoardCategory.DigitalInputBoard,
				BoardId: digitalInputBoard.DigitalInputBoardId,
				PortName: digitalInputBoard.DigitalInputBoardComPort,
				BaudRate: digitalInputBoard.DigitalInputBoardBaud);

			desiredConnections[definition.ConnectionKey] = definition;
		}

		return desiredConnections;
	}

	private static GpioBoardSerialConnection OpenConnection(GpioBoardSerialConnectionDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);

		var serialPort = new SerialPortInput();
		serialPort.ConnectionStatusChanged += (_, args) =>
		{
			Console.WriteLine($"[gpio-controller] Serial connection for {definition.BoardCategory.ToLogLabel()} '{definition.BoardId}' connected={args.Connected}.");
		};

		serialPort.MessageReceived += (_, args) =>
		{
			Console.WriteLine($"[gpio-controller] Serial message received for {definition.BoardCategory.ToLogLabel()} '{definition.BoardId}' ({args.Data.Length} bytes).");
		};

		serialPort.SetPort(definition.PortName, definition.BaudRate);
		var connected = serialPort.Connect();
		Console.WriteLine($"[gpio-controller] Opened serial port {definition.PortName} at {definition.BaudRate} for {definition.BoardCategory.ToLogLabel()} '{definition.BoardId}'. Connected={connected}.");

		return new GpioBoardSerialConnection(definition, serialPort);
	}

	private void RemoveConnection(string connectionKey)
	{
		if (!_connections.Remove(connectionKey, out var connection))
		{
			return;
		}

		try
		{
			connection.SerialPort.Disconnect();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[gpio-controller] Serial disconnect failed for {connection.Definition.BoardCategory.ToLogLabel()} '{connection.Definition.BoardId}': {ex.Message}");
		}

		Console.WriteLine($"[gpio-controller] Closed serial port {connection.Definition.PortName} for {connection.Definition.BoardCategory.ToLogLabel()} '{connection.Definition.BoardId}'.");
	}
}

internal sealed record GpioBoardSerialConnectionDefinition(
	GpioBoardCategory BoardCategory,
	string BoardId,
	string PortName,
	int BaudRate)
{
	public string ConnectionKey => $"{BoardCategory}:{BoardId}";
}

internal sealed record GpioBoardSerialConnection(
	GpioBoardSerialConnectionDefinition Definition,
	SerialPortInput SerialPort);

internal enum GpioBoardCategory
{
	RelayBoard = 0,

	DigitalInputBoard = 1
}

internal static class GpioBoardCategoryExtensions
{
	public static string ToLogLabel(this GpioBoardCategory boardCategory)
	{
		return boardCategory switch
		{
			GpioBoardCategory.RelayBoard => "relay board",
			GpioBoardCategory.DigitalInputBoard => "digital input board",
			_ => throw new ArgumentOutOfRangeException(nameof(boardCategory), boardCategory, "Unsupported GPIO board category.")
		};
	}
}

internal static class GpioBoardType
{
	public const string UppercaseATrigger = "A trigger";

	public const string LowercaseATrigger = "a trigger";

	private static readonly HashSet<string> SupportedBoardTypes = new(StringComparer.Ordinal)
	{
		UppercaseATrigger,
		LowercaseATrigger
	};

	public static bool IsSupported(string boardType)
	{
		return SupportedBoardTypes.Contains(boardType);
	}
}

internal sealed class GpioControllerConfigStore
{
	private const string ConfigFileName = "gpio-controller.config.json";

	private const string ConfigDirectoryName = "myforce";

	private readonly IGpioControllerStoredConfig _storedConfig;

	public GpioControllerConfigStore()
	{
		var configPath = ResolveConfigPath();
		var configDirectory = Path.GetDirectoryName(configPath);
		if (!string.IsNullOrWhiteSpace(configDirectory))
		{
			Directory.CreateDirectory(configDirectory);
		}

		_storedConfig = new ConfigurationBuilder<IGpioControllerStoredConfig>()
			.UseJsonFile(configPath)
			.Build();
	}

	public GpioControllerConfigPayload Load()
	{
		var configJson = _storedConfig.GpioControllerConfigJson;
		if (string.IsNullOrWhiteSpace(configJson))
		{
			return GpioControllerConfigPayload.Empty;
		}

		return GpioControllerJson.Deserialize<GpioControllerConfigPayload>(configJson) ?? GpioControllerConfigPayload.Empty;
	}

	public void Save(GpioControllerConfigPayload config)
	{
		ArgumentNullException.ThrowIfNull(config);

		_storedConfig.GpioControllerConfigJson = GpioControllerJson.Serialize(config);
		_storedConfig.LastUpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
	}

	public void Clear()
	{
		_storedConfig.GpioControllerConfigJson = string.Empty;
		_storedConfig.LastUpdatedUtc = string.Empty;
	}

	public IGpioControllerStoredConfig StoredConfig => _storedConfig;

	private static string ResolveConfigPath()
	{
		var appConfigDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		if (!string.IsNullOrWhiteSpace(appConfigDirectory))
		{
			return Path.Combine(appConfigDirectory, ConfigDirectoryName, ConfigFileName);
		}

		return Path.Combine(AppContext.BaseDirectory, ConfigFileName);
	}
}

public interface IGpioControllerStoredConfig
{
	string? MqttHost { get; set; }

	string? MqttPort { get; set; }

	string? MqttClientId { get; set; }

	string? MqttUseTls { get; set; }

	string? MqttUsername { get; set; }

	string? MqttPassword { get; set; }

	string? GpioControllerConfigJson { get; set; }

	string? LastUpdatedUtc { get; set; }
}

internal sealed class GpioControllerTopicFactory
{
	private const string LegacyRootTopic = "myforce/gpio";

	private const string ModuleTopic = "myforce/module/gpio.controller";

	public string AllCommandsTopicFilter => $"{ModuleTopic}/cmd/#";

	public string ApplyConfigTopic => $"{ModuleTopic}/cmd/config";

	public string ClearConfigTopic => $"{ModuleTopic}/cmd/clear_config";

	public string ConfigStateTopic => $"{ModuleTopic}/config";

	public string ServiceRegistryTopic => $"{ModuleTopic}/registry";

	public string ServiceStatusTopic => $"{ModuleTopic}/status";

	public string LegacyAllCommandsTopicFilter => $"{LegacyRootTopic}/cmd/#";

	public string LegacyApplyConfigTopic => $"{LegacyRootTopic}/cmd/config/apply";

	public string LegacyClearConfigTopic => $"{LegacyRootTopic}/cmd/config/clear";

	public string LegacyConfigStateTopic => $"{LegacyRootTopic}/state/config";

	public string LegacyServiceRegistryTopic => $"{LegacyRootTopic}/registry/service";

	public string LegacyServiceStatusTopic => $"{LegacyRootTopic}/status/service";
}

internal sealed record MqttLastWillMessage(string Topic, string Payload, bool Retain);

internal sealed record GpioControllerConfigPayload(
	IReadOnlyList<RelayBoardConfigPayload> RelayBoards,
	IReadOnlyList<DigitalInputBoardConfigPayload> DigitalInputBoards)
{
	public static GpioControllerConfigPayload Empty { get; } = new(Array.Empty<RelayBoardConfigPayload>(), Array.Empty<DigitalInputBoardConfigPayload>());
}

internal sealed record RelayBoardConfigPayload(
	string RelayBoardId,
	string RelayBoardType,
	int RelayBoardNumberOfChannels,
	string RelayBoardComPort,
	int RelayBoardBaud,
	IReadOnlyList<RelayBoardChannelConfigPayload> RelayBoardChannel);

internal sealed record RelayBoardChannelConfigPayload(
	string RelayBoardChannelId,
	string RelayBoardChannelFunction);

internal sealed record DigitalInputBoardConfigPayload(
	string DigitalInputBoardId,
	string DigitalInputBoardType,
	int DigitalInputBoardNumberOfChannels,
	string DigitalInputBoardComPort,
	int DigitalInputBoardBaud,
	IReadOnlyList<DigitalInputBoardChannelConfigPayload> DigitalInputBoardChannel);

internal sealed record DigitalInputBoardChannelConfigPayload(
	string DigitalInputBoardChannelId,
	string DigitalInputBoardChannelFunction);

internal sealed record GpioControllerServiceRegistryPayload(
	string ServiceId,
	string DisplayName,
	IReadOnlyList<string> CommandTopics,
	IReadOnlyList<string> StateTopics)
{
	public static GpioControllerServiceRegistryPayload Create(GpioControllerTopicFactory topics)
	{
		ArgumentNullException.ThrowIfNull(topics);

		return new GpioControllerServiceRegistryPayload(
			ServiceId: "gpio-controller",
			DisplayName: "GPIO Controller",
			CommandTopics: new[]
			{
				topics.ApplyConfigTopic,
				topics.ClearConfigTopic
			},
			StateTopics: new[]
			{
				topics.ConfigStateTopic,
				topics.ServiceStatusTopic
			});
	}
}

internal sealed record GpioControllerServiceStatusPayload(
	string ServiceId,
	GpioControllerServiceState State,
	int RelayBoardCount,
	int DigitalInputBoardCount,
	string Detail)
{
	public static GpioControllerServiceStatusPayload CreateRunning(string serviceId, int relayBoardCount, int digitalInputBoardCount, string detail)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
		ArgumentException.ThrowIfNullOrWhiteSpace(detail);

		return new GpioControllerServiceStatusPayload(serviceId, GpioControllerServiceState.Running, relayBoardCount, digitalInputBoardCount, detail);
	}

	public static GpioControllerServiceStatusPayload CreateStopped(string serviceId, int relayBoardCount, int digitalInputBoardCount, string detail)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
		ArgumentException.ThrowIfNullOrWhiteSpace(detail);

		return new GpioControllerServiceStatusPayload(serviceId, GpioControllerServiceState.Stopped, relayBoardCount, digitalInputBoardCount, detail);
	}
}

internal enum GpioControllerServiceState
{
	Stopped = 0,

	Running = 1
}

internal static class GpioControllerJson
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	public static string Serialize<T>(T value)
	{
		return JsonSerializer.Serialize(value, SerializerOptions);
	}

	public static T? Deserialize<T>(ReadOnlySequence<byte> payload)
	{
		if (payload.IsEmpty)
		{
			return default;
		}

		return JsonSerializer.Deserialize<T>(payload.ToArray(), SerializerOptions);
	}

	public static T? Deserialize<T>(string payload)
	{
		if (string.IsNullOrWhiteSpace(payload))
		{
			return default;
		}

		return JsonSerializer.Deserialize<T>(payload, SerializerOptions);
	}
}

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
		var configStore = new GpioControllerConfigStore();
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