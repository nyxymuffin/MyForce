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
using Config.Net;

var controller = new GpioControllerMqttApp();
await controller.RunAsync();

internal sealed class GpioControllerMqttApp
{
	private readonly MqttServiceRuntime _mqttRuntime;

	public GpioControllerMqttApp()
	{
		_mqttRuntime = new MqttServiceRuntime("gpio-controller");
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
		Console.WriteLine("GPIO Controller MQTT ready.");
		await _mqttRuntime.RunUntilStoppedAsync(cts.Token);
	}
}

internal sealed class MqttServiceRuntime : IAsyncDisposable
{
	private readonly IMqttClient _client;

	private readonly MqttServiceOptions _options;

	private readonly string _serviceName;

	public MqttServiceRuntime(string serviceName)
	{
		_serviceName = serviceName;
		_options = MqttServiceOptions.FromEnvironment(serviceName);
		_client = new MqttClientFactory().CreateMqttClient();
		_client.ConnectedAsync += OnConnectedAsync;
		_client.DisconnectedAsync += OnDisconnectedAsync;
		_client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
	}

	public async Task ConnectAsync(CancellationToken cancellationToken)
	{
		var optionsBuilder = new MqttClientOptionsBuilder()
			.WithProtocolVersion(MqttProtocolVersion.V500)
			.WithClientId(_options.ClientId)
			.WithTcpServer(_options.Host, _options.Port)
			.WithCleanSession();

		if (!string.IsNullOrWhiteSpace(_options.Username))
		{
			optionsBuilder = optionsBuilder.WithCredentials(_options.Username, _options.Password);
		}

		if (_options.UseTls)
		{
			optionsBuilder = optionsBuilder.WithTlsOptions(static builder => builder.UseTls());
		}

		await _client.ConnectAsync(optionsBuilder.Build(), cancellationToken);
	}

	public async Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(topicFilter);

		var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
			.WithTopicFilter(topicFilter)
			.Build();

		await _client.SubscribeAsync(subscribeOptions, cancellationToken);
		Console.WriteLine($"[{_serviceName}] Subscribed: {topicFilter}");
	}

	public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(topic);

		var message = new MqttApplicationMessageBuilder()
			.WithTopic(topic)
			.WithPayload(payload)
			.WithRetainFlag(retain)
			.Build();

		await _client.PublishAsync(message, cancellationToken);
		Console.WriteLine($"[{_serviceName}] Published: {topic}");
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
		_client.ConnectedAsync -= OnConnectedAsync;
		_client.DisconnectedAsync -= OnDisconnectedAsync;
		_client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;

		if (_client.IsConnected)
		{
			await _client.DisconnectAsync();
		}

		_client.Dispose();
	}

	private Task OnConnectedAsync(MqttClientConnectedEventArgs arg)
	{
		Console.WriteLine($"[{_serviceName}] Connected to MQTT broker at {_options.Host}:{_options.Port}.");
		return Task.CompletedTask;
	}

	private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
	{
		var detail = arg.Exception?.Message ?? arg.ReasonString ?? "Disconnected.";
		Console.WriteLine($"[{_serviceName}] MQTT disconnected: {detail}");
		return Task.CompletedTask;
	}

	private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
	{
		Console.WriteLine($"[{_serviceName}] Received message on topic: {arg.ApplicationMessage.Topic}");
		return Task.CompletedTask;
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