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
using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MQTTnet;
using NAudio.Wave;
using MyForce.Contracts.Radio;

internal sealed class AudioProcessorCoordinator : IAsyncDisposable
{
	private const string AdminCredential = "2135";

	private const string VipPttOrigin = "vip";

	// Matches AudioProcessorPersistedTopology's camelCase store format so config write-back
	// round-trips with the boot-time read (§4.2).
	private static readonly JsonSerializerOptions PersistedTopologySerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	private readonly AudioProcessorRegistry _registry;

	private readonly AudioFrameworkCatalog _audioFramework;

	private readonly AudioProcessorConfigStore _configStore;

	private readonly InternetRadioPlaybackController _internetRadioController;

	private readonly AudioMixerState _mixerState;

	private readonly AudioProcessorRoutingState _routingState;

	private readonly AudioMatrixEngine _audioMatrixEngine;

	private readonly MqttServiceRuntime _mqttRuntime;

	private readonly AudioProcessorTopicFactory _topics;

	// Phase 1 (§3.6): the real-time engine, backend, built-in primitives, and TX sequencer.
	private readonly IAudioBackend _audioBackend;

	private readonly RealtimeAudioEngine _realtimeEngine;

	private readonly EngineTopology _engineTopology;

	private readonly RelayKeyingService _relayKeying;

	private readonly TxStateMachine _txStateMachine;

	// VOX detect primitive (§3.6.8): one per radio that declares VOX, plus its RX source index
	// and the latest Call Detect state the control-thread poll loop maintains.
	private readonly Dictionary<string, VoxDetector> _voxDetectors;

	private readonly Dictionary<string, int> _rxSourceIndexByRadio;

	private readonly Dictionary<string, bool> _callDetectByRadio = new(StringComparer.OrdinalIgnoreCase);

	// Radios whose operator-mic gate is currently open (TX). Guarded by _engineRoutingGate.
	private readonly HashSet<string> _openMicGates = new(StringComparer.OrdinalIgnoreCase);

	private readonly object _engineRoutingGate = new();

	private readonly CancellationTokenSource _lifetimeCts = new();

	private Task? _voxPollTask;

	private readonly RadioPluginCatalog _pluginCatalog;

	private readonly AudioProcessorPersistedTopology _persistedTopology;

	private readonly RadioModuleHostManager _radioModuleHostManager;

	public AudioProcessorCoordinator(MqttServiceRuntime mqttRuntime, AudioProcessorTopicFactory topics)
	{
		ArgumentNullException.ThrowIfNull(mqttRuntime);
		ArgumentNullException.ThrowIfNull(topics);

		_mqttRuntime = mqttRuntime;
		_topics = topics;
		_configStore = new AudioProcessorConfigStore();
		_pluginCatalog = RadioPluginCatalog.Load(AudioProcessorPluginDirectory.Resolve(_configStore.StoredConfig), AudioProcessorLog.Write);
		_persistedTopology = AudioProcessorPersistedTopology.Load(_configStore.StoredConfig);
		_registry = AudioProcessorRegistry.Create(_persistedTopology, _pluginCatalog.Modules, AudioProcessorLog.Write);
		_audioFramework = AudioFrameworkCatalog.CreateDefault(_registry.RadioIds, AudioFrameworkCatalog.DiscoverPlaybackDevices());
		_internetRadioController = new InternetRadioPlaybackController(_configStore);
		_mixerState = AudioMixerState.CreateDefault(_audioFramework.ChannelStrips);
		_routingState = AudioProcessorRoutingState.CreateDefault(_registry.RadioIds, ResolveInitialSpeakerDeviceId());
		_audioMatrixEngine = new AudioMatrixEngine(_mixerState.CurrentSnapshot, _routingState.CurrentSnapshot);
		_radioModuleHostManager = new RadioModuleHostManager(_registry.Radios, _pluginCatalog.Modules, AudioProcessorLog.Write);

		// Phase 1 real-time engine (§3.6): build the fixed source/sink topology, open a backend,
		// and wire the built-in keying/detect primitives and the TX state machine to it.
		_relayKeying = new RelayKeyingService(_persistedTopology.RelaySets, AudioProcessorLog.Write);
		var engineSetup = BuildEngineSetup(_registry.RadioIds, ResolveInitialSpeakerDeviceId());
		_engineTopology = engineSetup.Topology;
		_rxSourceIndexByRadio = engineSetup.RxSourceIndexByRadio;
		_audioBackend = CreateAudioBackend(EngineAudioFormat.Default);
		_audioBackend.Bind(engineSetup.CaptureDeviceIds, engineSetup.PlaybackDeviceIds);
		_audioBackend.DeviceHotplug += OnBackendDeviceHotplug;
		_realtimeEngine = new RealtimeAudioEngine(_audioBackend, _engineTopology, AudioProcessorLog.Write);
		_voxDetectors = BuildVoxDetectors(_registry.Radios);
		_txStateMachine = new TxStateMachine(
			_registry.Radios,
			KeyRadioAsync,
			UnkeyRadioAsync,
			TryGetTalkPermitReady,
			SetMicGate,
			PublishRadioModuleStateAsync,
			AudioProcessorLog.Write);
		_realtimeEngine.PublishRouting(BuildEngineRoutingSnapshot());
		_internetRadioController.SetOutputSpeaker(_routingState.CurrentSnapshot.SpeakerSink.DeviceId);
		AudioProcessorLog.Write("discovery", $"Audio framework initialized with {_audioFramework.Devices.Count(device => device.OutputEnabled && string.Equals(device.Role, "speaker", StringComparison.OrdinalIgnoreCase))} output speaker device(s).");
		AudioProcessorLog.Write("discovery", $"Discovered {_pluginCatalog.Modules.Count} radio plugin type(s) from '{_pluginCatalog.PluginDirectoryPath}'.");
		AudioProcessorLog.Write("config", $"Loaded {_persistedTopology.RadioDefinitions.Count} persisted radio definition(s) and {_persistedTopology.RelaySets.Count} relay set definition(s).");
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await _mqttRuntime.SubscribeAsync(_topics.AllCommandsTopicFilter, cancellationToken).ConfigureAwait(false);
		await _mqttRuntime.SubscribeAsync(_topics.AllModuleCommandsTopicFilter, cancellationToken).ConfigureAwait(false);
		await _mqttRuntime.SubscribeAsync(_topics.ConsolePttCommandTopicFilter, cancellationToken).ConfigureAwait(false);
		await _radioModuleHostManager.StartAsync(cancellationToken).ConfigureAwait(false);

		// Build the routing matrix and start the RT audio thread (§3.6.10 boot step 4), then begin
		// the control-thread VOX poll loop that turns engine RX levels into Call Detect (§3.6.8).
		_realtimeEngine.PublishRouting(BuildEngineRoutingSnapshot());
		_realtimeEngine.Start();
		_voxPollTask = RunVoxPollLoopAsync(_lifetimeCts.Token);

		await RestoreInternetRadioPlaybackAsync(cancellationToken).ConfigureAwait(false);
		await PublishBirthSnapshotAsync(cancellationToken).ConfigureAwait(false);
	}

internal static class AudioProcessorPluginDirectory
{
	private const string PluginDirectoryName = "plugins";
	private const string PluginRootDirectoryName = "myforce";

	public static string Resolve(IAudioProcessorStoredConfig storedConfig)
	{
		ArgumentNullException.ThrowIfNull(storedConfig);

		if (!string.IsNullOrWhiteSpace(storedConfig.PluginDirectoryPath))
		{
			return Path.GetFullPath(storedConfig.PluginDirectoryPath);
		}

		var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		if (!string.IsNullOrWhiteSpace(appDataDirectory))
		{
			return Path.Combine(appDataDirectory, PluginRootDirectoryName, PluginDirectoryName);
		}

		return Path.Combine(AppContext.BaseDirectory, PluginDirectoryName);
	}
}

internal sealed record ModuleRadioStateSpecPayload(
	int V,
	DateTimeOffset Ts,
	string Id,
	[property: JsonPropertyName("rx_active")] bool RxActive,
	[property: JsonPropertyName("tx_active")] bool TxActive,
	[property: JsonPropertyName("tx_source")] string TxSource,
	ChannelInfo? Channel,
	ZoneInfo? Zone,
	string? Mode,
	SignalInfo? Signal)
{
	public static ModuleRadioStateSpecPayload Create(RadioRuntimeDefinition radio, RadioTxState state, bool rxActive)
	{
		ArgumentNullException.ThrowIfNull(radio);
		ArgumentNullException.ThrowIfNull(state);

		var isTxActive = state.State is TxStatePhase.Keying or TxStatePhase.Transmitting or TxStatePhase.Tail;
		return new ModuleRadioStateSpecPayload(
			1,
			DateTimeOffset.UtcNow,
			radio.Id.Value,
			RxActive: rxActive,                       // Call Detect from the VOX primitive (§3.6.8)
			TxActive: isTxActive,
			TxSource: isTxActive ? "manual" : "idle",
			Channel: null,
			Zone: null,
			Mode: null,
			Signal: null);
	}
}

internal sealed record ConsoleTxStatePayload(
	int V,
	DateTimeOffset Ts,
	string? Holder,
	string? Target,
	string State)
{
	public static ConsoleTxStatePayload Create(TxStateMachine txController)
	{
		ArgumentNullException.ThrowIfNull(txController);
		var target = txController.ActiveManualTransmitRadioId?.Value;
		return new ConsoleTxStatePayload(1, DateTimeOffset.UtcNow, target is null ? null : "vip", target, target is null ? "idle" : "active");
	}
}

internal sealed class RadioPluginCatalog
{
	private RadioPluginCatalog(string pluginDirectoryPath, IReadOnlyList<DiscoveredRadioModule> modules)
	{
		PluginDirectoryPath = pluginDirectoryPath;
		Modules = modules;
	}

	public string PluginDirectoryPath { get; }

	public IReadOnlyList<DiscoveredRadioModule> Modules { get; }

	public static RadioPluginCatalog Load(string pluginDirectoryPath, Action<string, string> log)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectoryPath);
		ArgumentNullException.ThrowIfNull(log);

		try
		{
			Directory.CreateDirectory(pluginDirectoryPath);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			log("discovery", $"Radio plugin directory '{pluginDirectoryPath}' is unavailable: {ex.Message}. Continuing without external radio plugins.");
			return new RadioPluginCatalog(pluginDirectoryPath, Array.Empty<DiscoveredRadioModule>());
		}

		var modules = new List<DiscoveredRadioModule>();
		foreach (var assemblyPath in Directory.EnumerateFiles(pluginDirectoryPath, "*.dll", SearchOption.TopDirectoryOnly))
		{
			try
			{
				var loadContext = new AssemblyLoadContext($"radio-plugin:{Path.GetFileNameWithoutExtension(assemblyPath)}", isCollectible: true);
				var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
				var factoryType = assembly
					.GetTypes()
					.FirstOrDefault(type => !type.IsAbstract && typeof(IRadioModuleFactory).IsAssignableFrom(type));

				if (factoryType is null)
				{
					log("discovery", $"Skipped plugin '{assemblyPath}' because it did not expose an {nameof(IRadioModuleFactory)} implementation.");
					loadContext.Unload();
					continue;
				}

				if (Activator.CreateInstance(factoryType) is not IRadioModuleFactory factory)
				{
					log("discovery", $"Skipped plugin '{assemblyPath}' because the factory could not be instantiated.");
					loadContext.Unload();
					continue;
				}

				if (factory.ContractVersion != RadioContract.Version)
				{
					log("discovery", $"Skipped plugin '{assemblyPath}' because it targets contract version {factory.ContractVersion} instead of {RadioContract.Version}.");
					loadContext.Unload();
					continue;
				}

				modules.Add(new DiscoveredRadioModule(
					assemblyPath,
					loadContext,
					factory.TypeId,
					factory.DisplayName,
					factory.Version,
					factory.ConfigSchema,
					factory.Capabilities,
					factory));
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				log("discovery", $"Failed to load radio plugin '{assemblyPath}': {ex.Message}");
			}
		}

		return new RadioPluginCatalog(pluginDirectoryPath, modules.AsReadOnly());
	}
}

internal sealed record DiscoveredRadioModule(
	string AssemblyPath,
	AssemblyLoadContext LoadContext,
	string TypeId,
	string DisplayName,
	string Version,
	string ConfigSchema,
	RadioCapabilities Capabilities,
	IRadioModuleFactory Factory);

internal sealed class RadioModuleHostManager : IAsyncDisposable
{
	private readonly IReadOnlyList<RadioRuntimeDefinition> _radios;
	private readonly IReadOnlyList<DiscoveredRadioModule> _discoveredModules;
	private readonly Action<string, string> _log;
	private readonly List<HostedRadioModule> _hostedModules = [];

	public RadioModuleHostManager(IReadOnlyList<RadioRuntimeDefinition> radios, IReadOnlyList<DiscoveredRadioModule> discoveredModules, Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(radios);
		ArgumentNullException.ThrowIfNull(discoveredModules);
		ArgumentNullException.ThrowIfNull(log);

		_radios = radios;
		_discoveredModules = discoveredModules;
		_log = log;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		foreach (var radio in _radios.Where(static radio => radio.Kind is RadioRuntimeKind.Module or RadioRuntimeKind.AdvancedModule))
		{
			var discoveredModule = _discoveredModules.FirstOrDefault(module => string.Equals(module.TypeId, radio.TypeId, StringComparison.OrdinalIgnoreCase));
			if (discoveredModule is null)
			{
				_log("module", $"No plugin loaded for radio '{radio.Id.Value}' type '{radio.TypeId}'. Module remains declared but unavailable.");
				continue;
			}

			var host = new RadioModuleHost(radio.Id, _log);
			var module = discoveredModule.Factory.Create(host);
			// Pass only the RM-owned settings section as a JsonElement per the reconciled contract (§3.7.7).
			var applyResult = await module.ApplyConfigAsync(radio.Config.Settings.Deserialize<JsonElement>(), cancellationToken).ConfigureAwait(false);
			if (applyResult.Status != MyForce.Contracts.Radio.OperationStatus.Ok)
			{
				_log("module", $"Plugin '{radio.TypeId}' rejected initial config for '{radio.Id.Value}': {applyResult.Status}.");
			}

			await module.StartAsync(cancellationToken).ConfigureAwait(false);
			_hostedModules.Add(new HostedRadioModule(radio.Id, discoveredModule, host, module));
			_log("module", $"Started radio module '{radio.Id.Value}' using plugin '{discoveredModule.TypeId}' version {discoveredModule.Version}.");
		}
	}

	/// <summary>
	/// Resolves the live RM instance for a radio, or null if no plugin is hosting it. Used by the
	/// TX state machine to drive in-process RM keying (§3.6.3).
	/// </summary>
	public IRadioModule? TryGetModule(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return _hostedModules.FirstOrDefault(module => module.RadioId == radioId)?.Module;
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var hostedModule in _hostedModules.AsEnumerable().Reverse())
		{
			try
			{
				await hostedModule.Module.StopAsync(CancellationToken.None).ConfigureAwait(false);
				await hostedModule.Module.DisposeAsync().ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				_log("module", $"Stopping radio module '{hostedModule.RadioId.Value}' failed: {ex.Message}");
			}
		}

		_hostedModules.Clear();
	}
}

internal sealed record HostedRadioModule(RadioId RadioId, DiscoveredRadioModule DiscoveredModule, RadioModuleHost Host, IRadioModule Module);

internal sealed class RadioModuleHost : IModuleHost
{
	private readonly Action<string, string> _log;

	public RadioModuleHost(RadioId radioId, Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		ArgumentNullException.ThrowIfNull(log);
		RadioId = radioId;
		_log = log;
	}

	public RadioId RadioId { get; }

	public IControlTransport? ControlTransport => null;

	public float GetRxLevel() => 0f;

	public void ReportState(RadioStateReport state)
	{
		ArgumentNullException.ThrowIfNull(state);
		_log("module", $"Radio module '{RadioId.Value}' reported state.");
	}

	public void ReportDetect(bool rxActive)
	{
		_log("module", $"Radio module '{RadioId.Value}' detect={(rxActive ? "active" : "idle")}.");
	}

	public void EmitEvent(string name, JsonNode? data = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		_log("module", $"Radio module '{RadioId.Value}' emitted event '{name}'.");
	}

	public void Log(LogLevel level, string message)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(message);
		_log("module", $"{level}: {RadioId.Value}: {message}");
	}
}

internal sealed class AudioProcessorPersistedTopology
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	private AudioProcessorPersistedTopology(
		IReadOnlyList<PersistedRadioDefinition> radioDefinitions,
		IReadOnlyList<RelaySetDefinition> relaySets)
	{
		RadioDefinitions = radioDefinitions;
		RelaySets = relaySets;
	}

	public IReadOnlyList<PersistedRadioDefinition> RadioDefinitions { get; }

	public IReadOnlyList<RelaySetDefinition> RelaySets { get; }

	public static AudioProcessorPersistedTopology Load(IAudioProcessorStoredConfig storedConfig)
	{
		ArgumentNullException.ThrowIfNull(storedConfig);

		return new AudioProcessorPersistedTopology(
			DeserializeList<PersistedRadioDefinition>(storedConfig.RadioDefinitionsJson),
			DeserializeList<RelaySetDefinition>(storedConfig.RelaySetsJson));
	}

	private static IReadOnlyList<T> DeserializeList<T>(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return Array.Empty<T>();
		}

		try
		{
			return JsonSerializer.Deserialize<T[]>(json, SerializerOptions) ?? Array.Empty<T>();
		}
		catch (JsonException)
		{
			return Array.Empty<T>();
		}
	}
}

internal sealed record PersistedRadioDefinition(
	string RadioId,
	string TypeId,
	string DisplayName,
	string Kind,
	string? InstanceConfigJson);

internal sealed record RelaySetDefinition(
	string RelaySetId,
	string ComPort,
	int Baud,
	string Protocol,
	int ChannelCount);

	/// <summary>
	/// Reapplies retained subscriptions and republishes the current AP health snapshot after MQTT reconnects.
	/// </summary>
	public async Task HandleConnectedAsync(CancellationToken cancellationToken)
	{
		await _mqttRuntime.SubscribeAsync(_topics.AllCommandsTopicFilter, cancellationToken).ConfigureAwait(false);
		await _mqttRuntime.SubscribeAsync(_topics.AllModuleCommandsTopicFilter, cancellationToken).ConfigureAwait(false);
		await _mqttRuntime.SubscribeAsync(_topics.ConsolePttCommandTopicFilter, cancellationToken).ConfigureAwait(false);
		await PublishHeartbeatAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args)
	{
		ArgumentNullException.ThrowIfNull(args);

		var topic = args.ApplicationMessage.Topic;
		if (string.IsNullOrWhiteSpace(topic))
		{
			return;
		}

		if (AudioProcessorTopicFactory.TryParseModuleCommandTopic(topic, out var moduleId, out var commandName)
			&& await TryHandleSpecModuleCommandAsync(topic, moduleId, commandName, args.ApplicationMessage.Payload).ConfigureAwait(false))
		{
			return;
		}

		if (string.Equals(topic, _topics.OutputSpeakerCommandTopic, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(topic, _topics.AudioOutputCommandTopic, StringComparison.OrdinalIgnoreCase))
		{
			var msgId = GetMessageId(args.ApplicationMessage.Payload);
			if (!ValidateAdminCommand(args.ApplicationMessage.Payload, topic))
			{
				await PublishCommandAckAsync(topic, msgId, "rejected", "auth", "invalid_auth", "Admin authentication is required.").ConfigureAwait(false);
				return;
			}

			var command = AudioProcessorJson.Deserialize<OutputSpeakerCommand>(args.ApplicationMessage.Payload);
			if (command is null)
			{
				return;
			}

			ApplyOutputSpeaker(command.DeviceId);
			await PublishRoutingStateAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishInternetRadioStateAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishCommandAckAsync(topic, msgId, "ok", null, null, null).ConfigureAwait(false);
			return;
		}

		if (string.Equals(topic, _topics.AudioOutputConfigCommandTopic, StringComparison.OrdinalIgnoreCase))
		{
			var msgId = GetMessageId(args.ApplicationMessage.Payload);
			if (!ValidateAdminCommand(args.ApplicationMessage.Payload, topic))
			{
				await PublishCommandAckAsync(topic, msgId, "rejected", "auth", "invalid_auth", "Admin authentication is required.").ConfigureAwait(false);
				return;
			}

			var command = AudioProcessorJson.Deserialize<AudioOutputConfigCommand>(args.ApplicationMessage.Payload);
			if (command is null)
			{
				return;
			}

			ApplyAudioOutputConfig(command);
			await PublishRoutingStateAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishInternetRadioStateAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishCommandAckAsync(topic, msgId, "ok", null, null, null).ConfigureAwait(false);
			return;
		}

		if (string.Equals(topic, _topics.ManualPttRequestTopic, StringComparison.OrdinalIgnoreCase)
			|| IsConsolePttTopic(topic))
		{
			var request = CreateManualPttRequest(topic, args.ApplicationMessage.Payload);
			if (request is null)
			{
				return;
			}

			var outcome = await ApplyManualPttAsync(request, CancellationToken.None).ConfigureAwait(false);
			await PublishMixerStateAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishStatusAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishConsoleTxStateAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishCommandAckAsync(
				topic,
				request.MsgId,
				outcome.Accepted ? "ok" : "rejected",
				outcome.Accepted ? null : "ptt",
				outcome.Accepted ? null : "tx_rejected",
				outcome.Accepted ? null : outcome.Detail).ConfigureAwait(false);
			return;
		}

		if (string.Equals(topic, _topics.ChannelGainCommandTopic, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(topic, _topics.MediaGainCommandTopic, StringComparison.OrdinalIgnoreCase))
		{
			// Volume/gain is an OPERATING command (§4.6): no admin auth required.
			var msgId = GetMessageId(args.ApplicationMessage.Payload);
			var command = AudioProcessorJson.Deserialize<AudioChannelGainCommand>(args.ApplicationMessage.Payload);
			if (command is null)
			{
				return;
			}

			_mixerState.SetGain(command.ChannelId, command.Gain);
			_internetRadioController.SetOutputGain(command.ChannelId, command.Gain);
			await PublishMixerStateAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishCommandAckAsync(topic, msgId, "ok", null, null, null).ConfigureAwait(false);
			return;
		}

		if (string.Equals(topic, _topics.ChannelMuteCommandTopic, StringComparison.OrdinalIgnoreCase))
		{
			// Mute is an OPERATING command (volume, §4.6): no admin auth required.
			var msgId = GetMessageId(args.ApplicationMessage.Payload);
			var command = AudioProcessorJson.Deserialize<AudioChannelMuteCommand>(args.ApplicationMessage.Payload);
			if (command is null)
			{
				return;
			}

			_mixerState.SetMuted(command.ChannelId, command.IsMuted);
			await PublishMixerStateAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishCommandAckAsync(topic, msgId, "ok", null, null, null).ConfigureAwait(false);
			return;
		}

		if (string.Equals(topic, _topics.InternetRadioPlayCommandTopic, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(topic, _topics.MediaPlayCommandTopic, StringComparison.OrdinalIgnoreCase))
		{
			var msgId = GetMessageId(args.ApplicationMessage.Payload);
			var command = AudioProcessorJson.Deserialize<InternetRadioPlayCommand>(args.ApplicationMessage.Payload);
			if (command is null)
			{
				return;
			}

			await _internetRadioController.PlayAsync(command, CancellationToken.None).ConfigureAwait(false);
			await PublishInternetRadioStateAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishCommandAckAsync(topic, msgId, "ok", null, null, null).ConfigureAwait(false);
			return;
		}

		if (string.Equals(topic, _topics.InternetRadioStopCommandTopic, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(topic, _topics.MediaStopCommandTopic, StringComparison.OrdinalIgnoreCase))
		{
			var msgId = GetMessageId(args.ApplicationMessage.Payload);
			_internetRadioController.Stop();
			await PublishInternetRadioStateAsync(CancellationToken.None).ConfigureAwait(false);
			await PublishCommandAckAsync(topic, msgId, "ok", null, null, null).ConfigureAwait(false);
		}
	}

	private async Task<bool> TryHandleSpecModuleCommandAsync(string topic, string moduleId, string commandName, ReadOnlySequence<byte> payload)
	{
		if (string.Equals(moduleId, _topics.AudioModuleId, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(moduleId, _topics.MediaModuleId, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var radio = _registry.Radios.FirstOrDefault(radio => string.Equals(radio.Id.Value, moduleId, StringComparison.OrdinalIgnoreCase));
		if (radio is null)
		{
			return false;
		}

		var envelope = AudioProcessorJson.Deserialize<MqttCommandEnvelope>(payload);
		if (string.Equals(commandName, "config", StringComparison.OrdinalIgnoreCase))
		{
			if (!ValidateAdminCommand(payload, topic))
			{
				await PublishCommandAckAsync(topic, envelope?.MsgId, "rejected", "auth", "invalid_auth", "Admin authentication is required.").ConfigureAwait(false);
				return true;
			}

			var command = AudioProcessorJson.Deserialize<ModuleConfigCommandPayload>(payload);
			if (command is null || !string.Equals(command.Id, moduleId, StringComparison.OrdinalIgnoreCase))
			{
				await PublishCommandAckAsync(topic, envelope?.MsgId, "rejected", "id", "invalid_module", "Module config command id did not match the topic module id.").ConfigureAwait(false);
				return true;
			}

			// Persist the accepted config to the System Config Store and mirror it back retained
			// (§4.2/§4.4). The RM-owned settings section is the new truth on the next boot; live
			// re-apply of disruptive common-section changes is deferred (§3.6.9).
			var acceptedSettings = command.Config["settings"] as JsonObject ?? command.Config;
			var mergedConfig = radio.Config with { Settings = (JsonObject)acceptedSettings.DeepClone() };
			PersistRadioInstanceConfig(radio, mergedConfig.Settings);
			AudioProcessorLog.Write("config", $"Persisted config for module '{moduleId}' to the System Config Store.");
			await _mqttRuntime.PublishAsync(
				_topics.ModuleConfigTopic(radio.Id),
				AudioProcessorJson.Serialize(new ModuleConfigSpecPayload(1, DateTimeOffset.UtcNow, radio.Id.Value, RadioInstanceConfigPayload.Create(mergedConfig))),
				retain: true,
				cancellationToken: CancellationToken.None).ConfigureAwait(false);
			await PublishCommandAckAsync(topic, envelope?.MsgId, "ok", null, null, null).ConfigureAwait(false);
			return true;
		}

		if (!radio.Capabilities.Controls.Contains(commandName, StringComparer.OrdinalIgnoreCase))
		{
			await PublishCommandAckAsync(topic, envelope?.MsgId, "rejected", "action", "unsupported_action", $"Module '{moduleId}' does not support action '{commandName}'.").ConfigureAwait(false);
			return true;
		}

		AudioProcessorLog.Write("control", $"Accepted control action '{commandName}' for module '{moduleId}'. Module execution is staged for hosted RMs.");
		await PublishCommandAckAsync(topic, envelope?.MsgId, "ok", null, null, null).ConfigureAwait(false);
		return true;
	}

	private async Task PublishCommandAckAsync(string commandTopic, string? msgId, string status, string? field, string? code, string? message)
	{
		if (string.IsNullOrWhiteSpace(msgId))
		{
			return;
		}

		var errors = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
			? null
			: new[] { new CommandAckErrorPayload(field, code ?? status, message ?? status) };
		var ack = new CommandAckPayload(1, DateTimeOffset.UtcNow, msgId, status, errors);
		await _mqttRuntime.PublishAsync(
			$"{commandTopic}/ack",
			AudioProcessorJson.Serialize(ack),
			retain: false,
			cancellationToken: CancellationToken.None).ConfigureAwait(false);
	}

	private static string? GetMessageId(ReadOnlySequence<byte> payload)
	{
		return TryReadCommandEnvelope(payload, out var envelope) ? envelope.MsgId : null;
	}

	private bool ValidateAdminCommand(ReadOnlySequence<byte> payload, string topic)
	{
		if (TryReadCommandEnvelope(payload, out var envelope)
			&& string.Equals(envelope.Auth, AdminCredential, StringComparison.Ordinal))
		{
			return true;
		}

		AudioProcessorLog.Write("auth", $"Rejected admin command on '{topic}' because the auth credential was missing or invalid.");
		return false;
	}

	private static bool TryReadCommandEnvelope(ReadOnlySequence<byte> payload, out MqttCommandEnvelope envelope)
	{
		envelope = new MqttCommandEnvelope(null, null);
		if (payload.IsEmpty)
		{
			return false;
		}

		try
		{
			using var document = JsonDocument.Parse(payload.ToArray());
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			var msgId = TryGetStringProperty(document.RootElement, "msg_id") ?? TryGetStringProperty(document.RootElement, "msgId");
			var auth = TryGetStringProperty(document.RootElement, "auth");
			envelope = new MqttCommandEnvelope(msgId, auth);
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static string? TryGetStringProperty(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var property))
		{
			return null;
		}

		return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
	}

	private static bool IsConsolePttTopic(string topic)
	{
		return topic.StartsWith("myforce/console/", StringComparison.OrdinalIgnoreCase)
			&& topic.EndsWith("/cmd/ptt", StringComparison.OrdinalIgnoreCase);
	}

	private async Task RestoreInternetRadioPlaybackAsync(CancellationToken cancellationToken)
	{
		var storedCommand = _internetRadioController.GetStoredPlayCommand();
		if (storedCommand is null)
		{
			AudioProcessorLog.Write("playback", "No persisted internet radio stream found for startup restore.");
			return;
		}

		try
		{
			AudioProcessorLog.Write("playback", $"Restoring persisted internet radio stream '{storedCommand.DisplayName}' from {storedCommand.StreamUrl}.");
			await _internetRadioController.PlayAsync(storedCommand, cancellationToken).ConfigureAwait(false);
			AudioProcessorLog.Write("playback", "Persisted internet radio stream restored successfully.");
		}
		catch (Exception ex) when (ex is HttpRequestException or PlatformNotSupportedException or InvalidOperationException)
		{
			AudioProcessorLog.Write("playback", $"Internet radio restore skipped: {ex.Message}");
		}
	}

	private void ApplyOutputSpeaker(string deviceId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

		if (!_audioFramework.Devices.Any(device =>
				device.OutputEnabled
				&& string.Equals(device.Id.Value, deviceId, StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidOperationException($"Unknown output speaker device '{deviceId}'.");
		}

		_routingState.SetSpeakerSink(deviceId);
		_audioMatrixEngine.UpdateRouting(_routingState.CurrentSnapshot);
		AudioProcessorLog.Write("config", $"Applying AP master output speaker '{deviceId}'.");
		_configStore.StoredConfig.OutputSpeakerDeviceId = deviceId;
		_internetRadioController.SetOutputSpeaker(deviceId);
		AudioProcessorLog.Write("config", $"AP master output speaker persisted as '{deviceId}'.");
	}

	private void ApplyAudioOutputConfig(AudioOutputConfigCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);
		ApplyOutputSpeaker(command.DeviceId);
	}

	private string ResolveInitialSpeakerDeviceId()
	{
		var configuredDeviceId = _configStore.StoredConfig.OutputSpeakerDeviceId;
		if (!string.IsNullOrWhiteSpace(configuredDeviceId)
			&& _audioFramework.Devices.Any(device =>
				device.OutputEnabled
				&& string.Equals(device.Id.Value, configuredDeviceId, StringComparison.OrdinalIgnoreCase)))
		{
			return configuredDeviceId;
		}

		return AudioFrameworkCatalog.DefaultSpeakerDeviceId;
	}

	public async ValueTask DisposeAsync()
	{
		// Stop the control-thread VOX loop and the RT engine before tearing down dependents (§3.6.6).
		await _lifetimeCts.CancelAsync().ConfigureAwait(false);
		if (_voxPollTask is not null)
		{
			try
			{
				await _voxPollTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		_realtimeEngine.Dispose();
		_audioBackend.DeviceHotplug -= OnBackendDeviceHotplug;
		_audioBackend.Dispose();
		_relayKeying.Dispose();
		await _txStateMachine.DisposeAsync().ConfigureAwait(false);
		await _radioModuleHostManager.DisposeAsync().ConfigureAwait(false);
		await _internetRadioController.DisposeAsync().ConfigureAwait(false);
		_lifetimeCts.Dispose();
	}

	/// <summary>
	/// Publishes a recurring AP heartbeat so the UI can actively detect stale component status.
	/// </summary>
	public async Task PublishHeartbeatAsync(CancellationToken cancellationToken)
	{
		await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Handles a manual PTT request through the §3.4 TX state machine. Origin is restricted to the
	/// VIP physical button (§5.8.6); the legacy mixer/routing state is kept in sync only for the
	/// MQTT display payloads, while the real audio gate is opened by the state machine's gate
	/// callback (<see cref="SetMicGate"/>).
	/// </summary>
	private async Task<TxOutcome> ApplyManualPttAsync(ManualPttRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (!string.Equals(request.Origin, VipPttOrigin, StringComparison.OrdinalIgnoreCase))
		{
			AudioProcessorLog.Write("tx", $"Manual PTT rejected for '{request.RadioId.Value}' because origin '{request.Origin ?? "<missing>"}' is not authorized.");
			return TxOutcome.Rejected("PTT is restricted to the VIP physical button.");
		}

		if (!_registry.Radios.Any(radio => radio.Id == request.RadioId))
		{
			return TxOutcome.Rejected($"Unknown radio '{request.RadioId.Value}'.");
		}

		if (request.IsPressed)
		{
			var keyOutcome = await _txStateMachine.RequestKeyAsync(request.RadioId, request.IsOverride, cancellationToken).ConfigureAwait(false);
			if (keyOutcome.Accepted)
			{
				// Mirror into the legacy mixer/routing state purely for the MQTT display (§3.9.2).
				_routingState.SetOperatorMicTarget(request.RadioId);
				_mixerState.SetChannelActive(AudioChannelId.OperatorMic, true);
				_mixerState.SetTransmitTarget(request.RadioId, true);
			}

			return keyOutcome;
		}

		var unkeyOutcome = await _txStateMachine.RequestUnkeyAsync(request.RadioId, cancellationToken).ConfigureAwait(false);
		if (unkeyOutcome.Accepted)
		{
			_routingState.ClearOperatorMicTarget(request.RadioId);
			_mixerState.SetTransmitTarget(request.RadioId, false);
			if (_txStateMachine.ActiveManualTransmitRadioId is null)
			{
				_mixerState.SetChannelActive(AudioChannelId.OperatorMic, false);
			}
		}

		return unkeyOutcome;
	}

	// ---- Phase 1 engine wiring (§3.6): topology, backend, keying, VOX, gate ----

	/// <summary>
	/// Builds the fixed engine source/sink topology from the declared radios (§3.6.10): source 0 is
	/// the operator mic, then one RX per radio; sink 0 is the speaker, then one TX per radio. Each
	/// endpoint binds to a backend port at the same index, so a slot is stable across unplug (§3.6.10).
	/// </summary>
	private static EngineSetupResult BuildEngineSetup(IReadOnlyList<RadioId> radioIds, string speakerDeviceId)
	{
		ArgumentNullException.ThrowIfNull(radioIds);
		ArgumentException.ThrowIfNullOrWhiteSpace(speakerDeviceId);

		var sources = new List<EngineEndpoint> { new("mic", EngineEndpointKind.OperatorMic, 0) };
		var sinks = new List<EngineEndpoint> { new("spk", EngineEndpointKind.Speaker, 0) };
		var captureDeviceIds = new List<string> { "operator-mic" };
		var playbackDeviceIds = new List<string> { speakerDeviceId };
		var rxSourceIndexByRadio = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		for (var index = 0; index < radioIds.Count; index++)
		{
			var radioId = radioIds[index];
			var port = index + 1; // port 0 is the mic / speaker
			sources.Add(new EngineEndpoint($"rx:{radioId.Value}", EngineEndpointKind.RadioRx, port));
			sinks.Add(new EngineEndpoint($"tx:{radioId.Value}", EngineEndpointKind.RadioTx, port));
			captureDeviceIds.Add($"radio-{radioId.Value}");
			playbackDeviceIds.Add($"radio-{radioId.Value}");
			rxSourceIndexByRadio[radioId.Value] = port;
		}

		return new EngineSetupResult(new EngineTopology(sources, sinks), captureDeviceIds, playbackDeviceIds, rxSourceIndexByRadio);
	}

	/// <summary>
	/// Selects the concrete backend (§3.6.4): ALSA on the in-vehicle Linux target, the portable
	/// null backend elsewhere so the engine still runs on the dev box. PipeWire is a future backend
	/// behind the same interface.
	/// </summary>
	private static IAudioBackend CreateAudioBackend(EngineAudioFormat format)
	{
		if (OperatingSystem.IsLinux())
		{
			return new AlsaAudioBackend(format, AudioProcessorLog.Write);
		}

		AudioProcessorLog.Write("engine", "Non-Linux host: using the null audio backend (no device I/O). ALSA/PipeWire is used on the in-vehicle target.");
		return new NullAudioBackend(format);
	}

	private static Dictionary<string, VoxDetector> BuildVoxDetectors(IReadOnlyList<RadioRuntimeDefinition> radios)
	{
		ArgumentNullException.ThrowIfNull(radios);

		var detectors = new Dictionary<string, VoxDetector>(StringComparer.OrdinalIgnoreCase);
		foreach (var radio in radios.Where(radio => radio.Config.Detect.Method == MyForce.Contracts.Radio.DetectMethod.Vox))
		{
			var vox = radio.Config.Detect.Vox;
			detectors[radio.Id.Value] = new VoxDetector(vox?.ThresholdDb ?? -45d, vox?.AttackMs ?? 20, vox?.HangMs ?? 250);
		}

		return detectors;
	}

	/// <summary>Asserts keying for a radio: relay built-in or in-process RM call (§3.6.3). Returns success.</summary>
	private async Task<bool> KeyRadioAsync(RadioId radioId, CancellationToken cancellationToken)
	{
		var radio = _registry.Radios.First(radio => radio.Id == radioId);
		if (radio.Config.Keying.Method == MyForce.Contracts.Radio.KeyingMethod.Relay)
		{
			var relaySetId = radio.Config.Keying.Relay?.RelaySet ?? $"auto-{radioId.Value}";
			var channel = radio.Config.Keying.Relay?.Channel ?? 1;
			return _relayKeying.Assert(relaySetId, channel);
		}

		if (_radioModuleHostManager.TryGetModule(radioId) is IKeyingProvider provider)
		{
			// In-process RM keying; talk-permit readiness (if any) is reported via RadioStateReport.Ready (§3.4).
			await provider.KeyAsync(cancellationToken).ConfigureAwait(false);
			return true;
		}

		AudioProcessorLog.Write("tx", $"Radio '{radioId.Value}' declares RM keying but no plugin keying provider is loaded; using a virtual key.");
		return true;
	}

	/// <summary>Releases keying for a radio (relay deassert or RM UnkeyAsync, §3.6.3).</summary>
	private async Task UnkeyRadioAsync(RadioId radioId, CancellationToken cancellationToken)
	{
		var radio = _registry.Radios.First(radio => radio.Id == radioId);
		if (radio.Config.Keying.Method == MyForce.Contracts.Radio.KeyingMethod.Relay)
		{
			var relaySetId = radio.Config.Keying.Relay?.RelaySet ?? $"auto-{radioId.Value}";
			var channel = radio.Config.Keying.Relay?.Channel ?? 1;
			_relayKeying.Deassert(relaySetId, channel);
			return;
		}

		if (_radioModuleHostManager.TryGetModule(radioId) is IKeyingProvider provider)
		{
			await provider.UnkeyAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Talk-permit readiness for trunked/digital radios (§3.4). Null = not reported by any RM yet.</summary>
	private bool? TryGetTalkPermitReady(RadioId radioId) => null;

	/// <summary>
	/// Opens or closes a radio's operator-mic gate by republishing the routing snapshot to the RT
	/// engine (§3.4 TX step / un-key step). Refuses to open onto an Unavailable TX port (§3.6.10).
	/// </summary>
	private void SetMicGate(RadioId radioId, bool open)
	{
		lock (_engineRoutingGate)
		{
			if (open)
			{
				if (_engineTopology.TryGetSinkIndex($"tx:{radioId.Value}", out var sinkIndex)
					&& !_audioBackend.IsPortAvailable(AudioPortDirection.Playback, sinkIndex))
				{
					AudioProcessorLog.Write("tx", $"Mic gate not opened for '{radioId.Value}' because its TX device is Unavailable.");
					return;
				}

				_openMicGates.Add(radioId.Value);
			}
			else
			{
				_openMicGates.Remove(radioId.Value);
			}
		}

		_realtimeEngine.PublishRouting(BuildEngineRoutingSnapshot());
	}

	/// <summary>
	/// Constructs the current routing snapshot: every radio RX monitored to the speaker at unity,
	/// and the operator mic routed to the TX of each open-gated radio (§3.5). Bridge mix-minus is
	/// Phase 2; this covers manual TX and RX monitoring.
	/// </summary>
	private EngineRoutingSnapshot BuildEngineRoutingSnapshot()
	{
		var builder = new EngineRoutingBuilder(_engineTopology);
		foreach (var radioId in _registry.RadioIds)
		{
			builder.SetGain($"rx:{radioId.Value}", "spk", 1.0f);
		}

		string[] openGates;
		lock (_engineRoutingGate)
		{
			openGates = _openMicGates.ToArray();
		}

		foreach (var radioValue in openGates)
		{
			builder.SetGain("mic", $"tx:{radioValue}", 1.0f);
		}

		return builder.Build();
	}

	/// <summary>Publishes a radio's retained module state (TX phase merged with VOX Call Detect, §5.8.5).</summary>
	private async Task PublishRadioModuleStateAsync(RadioId radioId)
	{
		var radio = _registry.Radios.FirstOrDefault(radio => radio.Id == radioId);
		if (radio is null)
		{
			return;
		}

		var rxActive = _callDetectByRadio.TryGetValue(radioId.Value, out var detected) && detected;
		await _mqttRuntime.PublishAsync(
			_topics.ModuleStateTopic(radioId),
			AudioProcessorJson.Serialize(ModuleRadioStateSpecPayload.Create(radio, _txStateMachine.GetState(radioId), rxActive)),
			retain: true,
			cancellationToken: CancellationToken.None).ConfigureAwait(false);
	}

	/// <summary>
	/// Writes a radio's accepted instance config into the System Config Store (§4.2). The full
	/// declared topology is re-serialized so other radios' definitions are preserved; on the next
	/// boot the store rehydrates this config as the truth.
	/// </summary>
	private void PersistRadioInstanceConfig(RadioRuntimeDefinition radio, JsonObject settings)
	{
		ArgumentNullException.ThrowIfNull(radio);
		ArgumentNullException.ThrowIfNull(settings);

		var definitions = LoadPersistedRadioDefinitionsForWrite();
		var updated = new PersistedRadioDefinition(
			radio.Id.Value,
			radio.TypeId,
			radio.DisplayName,
			radio.Kind.ToString(),
			settings.ToJsonString());

		var existingIndex = definitions.FindIndex(definition => string.Equals(definition.RadioId, radio.Id.Value, StringComparison.OrdinalIgnoreCase));
		if (existingIndex >= 0)
		{
			definitions[existingIndex] = updated;
		}
		else
		{
			definitions.Add(updated);
		}

		_configStore.StoredConfig.RadioDefinitionsJson = JsonSerializer.Serialize(definitions, PersistedTopologySerializerOptions);
	}

	/// <summary>
	/// Loads the persisted radio definitions for a write, seeding from the live registry the first
	/// time so a single config edit does not drop the rest of the declared topology.
	/// </summary>
	private List<PersistedRadioDefinition> LoadPersistedRadioDefinitionsForWrite()
	{
		var json = _configStore.StoredConfig.RadioDefinitionsJson;
		if (!string.IsNullOrWhiteSpace(json))
		{
			try
			{
				var stored = JsonSerializer.Deserialize<List<PersistedRadioDefinition>>(json, PersistedTopologySerializerOptions);
				if (stored is { Count: > 0 })
				{
					return stored;
				}
			}
			catch (JsonException)
			{
				AudioProcessorLog.Write("config", "Stored radio definitions were invalid JSON; reseeding from the live registry.");
			}
		}

		return _registry.Radios
			.Select(radio => new PersistedRadioDefinition(
				radio.Id.Value,
				radio.TypeId,
				radio.DisplayName,
				radio.Kind.ToString(),
				radio.Config.Settings.ToJsonString()))
			.ToList();
	}

	/// <summary>
	/// Reacts to a backend hotplug edge (§3.6.10): a returning device re-activates its slot, a lost
	/// device marks the radio Unavailable. The matrix slot itself is unchanged.
	/// </summary>
	private void OnBackendDeviceHotplug(object? sender, AudioDeviceHotplugEventArgs args)
	{
		const string radioPrefix = "radio-";
		if (!args.DeviceId.StartsWith(radioPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		var radioValue = args.DeviceId[radioPrefix.Length..];
		var radio = _registry.Radios.FirstOrDefault(radio => string.Equals(radio.Id.Value, radioValue, StringComparison.OrdinalIgnoreCase));
		if (radio is null)
		{
			return;
		}

		AudioProcessorLog.Write("engine", $"Radio '{radio.Id.Value}' device {(args.IsPresent ? "returned (Available)" : "lost (Unavailable)")}.");
		_ = _mqttRuntime.PublishAsync(
			_topics.ModuleStatusTopic(radio.Id),
			AudioProcessorJson.Serialize(ModuleStatusSpecPayload.CreateForHealth(radio, args.IsPresent)),
			retain: true,
			cancellationToken: CancellationToken.None);
	}

	/// <summary>
	/// Control-thread loop that turns each radio's engine RX level into a debounced Call Detect via
	/// the VOX primitive (§3.6.8) and publishes rx_active when it changes. Runs off the RT path.
	/// </summary>
	private async Task RunVoxPollLoopAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				var nowMs = Environment.TickCount64;
				foreach (var (radioValue, detector) in _voxDetectors)
				{
					if (!_rxSourceIndexByRadio.TryGetValue(radioValue, out var sourceIndex))
					{
						continue;
					}

					var level = _realtimeEngine.GetSourceLevel(sourceIndex);
					var detected = detector.Update(level, nowMs);
					var previous = _callDetectByRadio.TryGetValue(radioValue, out var prior) && prior;
					if (detected != previous)
					{
						_callDetectByRadio[radioValue] = detected;
						await PublishRadioModuleStateAsync(new RadioId(radioValue)).ConfigureAwait(false);
					}
				}

				await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private ManualPttRequest? CreateManualPttRequest(string topic, ReadOnlySequence<byte> payload)
	{
		if (!IsConsolePttTopic(topic))
		{
			return AudioProcessorJson.Deserialize<ManualPttRequest>(payload);
		}

		var request = AudioProcessorJson.Deserialize<ConsolePttRequest>(payload);
		if (request is null)
		{
			return null;
		}

		if (string.IsNullOrWhiteSpace(request.Target))
		{
			AudioProcessorLog.Write("tx", "Console PTT rejected because target is missing.");
			return null;
		}

		var isPressed = request.State switch
		{
			"down" => true,
			"up" => false,
			_ => (bool?)null
		};

		if (isPressed is null)
		{
			AudioProcessorLog.Write("tx", $"Console PTT rejected because state '{request.State}' is not supported.");
			return null;
		}

		return new ManualPttRequest(
			new RadioId(request.Target),
			isPressed.Value,
			request.Origin,
			request.V.ToString(CultureInfo.InvariantCulture),
			request.Ts,
			request.MsgId,
			request.Auth,
			request.Override ?? false);
	}

	private async Task PublishBirthSnapshotAsync(CancellationToken cancellationToken)
	{
		await PublishSpecSystemTopicsAsync(cancellationToken).ConfigureAwait(false);

		await _mqttRuntime.PublishAsync(
			_topics.ServiceRegistryTopic,
			AudioProcessorJson.Serialize(ServiceRegistryPayload.Create(_registry)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		await PublishRadioRuntimeAsync(cancellationToken).ConfigureAwait(false);

		await _mqttRuntime.PublishAsync(
			_topics.RoutingStateTopic,
			AudioProcessorJson.Serialize(RoutingStatePayload.Create(_routingState.CurrentSnapshot, _configStore.StoredConfig)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		await _mqttRuntime.PublishAsync(
			_topics.AudioFrameworkTopic,
			AudioProcessorJson.Serialize(AudioFrameworkPayload.Create(_audioFramework)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		await PublishMixerStateAsync(cancellationToken).ConfigureAwait(false);
		await PublishInternetRadioStateAsync(cancellationToken).ConfigureAwait(false);

		await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task PublishMixerStateAsync(CancellationToken cancellationToken)
	{
		_audioMatrixEngine.UpdateMixer(_mixerState.CurrentSnapshot);
		await _mqttRuntime.PublishAsync(
			_topics.AudioMixerStateTopic,
			AudioProcessorJson.Serialize(AudioMixerStatePayload.Create(_mixerState.CurrentSnapshot)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private async Task PublishRoutingStateAsync(CancellationToken cancellationToken)
	{
		await _mqttRuntime.PublishAsync(
			_topics.RoutingStateTopic,
			AudioProcessorJson.Serialize(RoutingStatePayload.Create(_routingState.CurrentSnapshot, _configStore.StoredConfig)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private async Task PublishStatusAsync(CancellationToken cancellationToken)
	{
		await _mqttRuntime.PublishAsync(
			_topics.ServiceStatusTopic,
			AudioProcessorJson.Serialize(
				ServiceStatusPayload.CreateRunning(
					serviceId: "audio-processor",
					radioCount: _registry.RadioIds.Count,
					bridgeCount: _registry.Bridges.Count,
					activeManualTransmitRadioId: _txStateMachine.ActiveManualTransmitRadioId?.Value)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	private async Task PublishSpecSystemTopicsAsync(CancellationToken cancellationToken)
	{
		await _mqttRuntime.PublishAsync(
			_topics.SystemPluginsTopic,
			AudioProcessorJson.Serialize(SystemPluginsPayload.Create(_pluginCatalog.Modules)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		await _mqttRuntime.PublishAsync(
			_topics.SystemDefinitionTopic,
			AudioProcessorJson.Serialize(SystemDefinitionPayload.Create(_registry)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Publishes retained per-radio config, capabilities, and runtime TX state for dynamic admin consumers.
	/// </summary>
	private async Task PublishRadioRuntimeAsync(CancellationToken cancellationToken)
	{
		await _mqttRuntime.PublishAsync(
			_topics.RadioRuntimeTopic,
			AudioProcessorJson.Serialize(RadioRuntimePayload.Create(_registry, _txStateMachine)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);

		foreach (var radio in _registry.Radios)
		{
			await _mqttRuntime.PublishAsync(
				_topics.ModuleRegistryTopic(radio.Id),
				AudioProcessorJson.Serialize(ModuleRegistrySpecPayload.Create(radio)),
				retain: true,
				cancellationToken: cancellationToken).ConfigureAwait(false);

			await _mqttRuntime.PublishAsync(
				_topics.ModuleConfigTopic(radio.Id),
				AudioProcessorJson.Serialize(ModuleConfigSpecPayload.Create(radio)),
				retain: true,
				cancellationToken: cancellationToken).ConfigureAwait(false);

			await _mqttRuntime.PublishAsync(
				_topics.ModuleStatusTopic(radio.Id),
				AudioProcessorJson.Serialize(ModuleStatusSpecPayload.CreateOnline(radio)),
				retain: true,
				cancellationToken: cancellationToken).ConfigureAwait(false);

			await _mqttRuntime.PublishAsync(
				_topics.ModuleStateTopic(radio.Id),
				AudioProcessorJson.Serialize(ModuleRadioStateSpecPayload.Create(radio, _txStateMachine.GetState(radio.Id), _callDetectByRadio.TryGetValue(radio.Id.Value, out var radioRxActive) && radioRxActive)),
				retain: true,
				cancellationToken: cancellationToken).ConfigureAwait(false);
		}


		await PublishConsoleTxStateAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task PublishConsoleTxStateAsync(CancellationToken cancellationToken)
	{
		await _mqttRuntime.PublishAsync(
			_topics.ConsoleTxTopic,
			AudioProcessorJson.Serialize(ConsoleTxStatePayload.Create(_txStateMachine)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Publishes the current retained internet-radio playback state for reconnecting UI clients.
	/// </summary>
	private async Task PublishInternetRadioStateAsync(CancellationToken cancellationToken)
	{
		await _mqttRuntime.PublishAsync(
			_topics.InternetRadioStateTopic,
			AudioProcessorJson.Serialize(InternetRadioStatePayload.Create(_internetRadioController.CurrentState)),
			retain: true,
			cancellationToken: cancellationToken).ConfigureAwait(false);
	}
}

internal sealed class AudioProcessorRegistry
{
	public AudioProcessorRegistry(IReadOnlyList<RadioRuntimeDefinition> radios, IReadOnlyList<BridgeDefinition> bridges)
	{
		ArgumentNullException.ThrowIfNull(radios);
		ArgumentNullException.ThrowIfNull(bridges);

		Radios = radios;
		Bridges = bridges;
	}

	/// <summary>
	/// Gets the declared radio resources and module-backed radios known to the AP.
	/// </summary>
	public IReadOnlyList<RadioRuntimeDefinition> Radios { get; }

	/// <summary>
	/// Gets the stable logical radio ids used by the mixer, routing, and TX controller.
	/// </summary>
	public IReadOnlyList<RadioId> RadioIds => Radios.Select(static radio => radio.Id).ToArray();

	public IReadOnlyList<BridgeDefinition> Bridges { get; }

	public static AudioProcessorRegistry Create(AudioProcessorCoordinator.AudioProcessorPersistedTopology topology, IReadOnlyList<AudioProcessorCoordinator.DiscoveredRadioModule> discoveredModules, Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(topology);
		ArgumentNullException.ThrowIfNull(discoveredModules);
		ArgumentNullException.ThrowIfNull(log);

		if (topology.RadioDefinitions.Count == 0)
		{
			log("config", "No persisted radio definitions found; using built-in starter topology.");
			return CreateDefault(discoveredModules, log);
		}

		var radios = new List<RadioRuntimeDefinition>();
		foreach (var definition in topology.RadioDefinitions)
		{
			var radio = CreatePersistedRadio(definition, discoveredModules, log);
			if (radio is not null)
			{
				radios.Add(radio);
			}
		}

		if (radios.Count == 0)
		{
			log("config", "Persisted radio definitions did not contain any usable modules; AP will publish an empty declared radio topology.");
		}

		return new AudioProcessorRegistry(radios.AsReadOnly(), Array.Empty<BridgeDefinition>());
	}

	public static AudioProcessorRegistry CreateDefault(IReadOnlyList<AudioProcessorCoordinator.DiscoveredRadioModule> discoveredModules, Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(discoveredModules);
		ArgumentNullException.ThrowIfNull(log);

		var radios = new List<RadioRuntimeDefinition>();

		// Module radios are declared only for plugins that were actually discovered: the AP must
		// report the status of the modules it has, not a fixed catalog of modules it might host.
		foreach (var module in discoveredModules)
		{
			radios.Add(CreateDiscoveredModuleRadio(module));
		}

		// The 4-wire resource is a built-in AP capability rather than a plugin, so it is always present.
		radios.Add(CreateResourceRadio(id: "4w", typeId: "4w_resource", displayName: "4-Wire Resource"));

		log("config", $"Built starter topology from {discoveredModules.Count} discovered module plugin(s) plus the built-in 4-wire resource.");
		return new AudioProcessorRegistry(radios.AsReadOnly(), Array.Empty<BridgeDefinition>());
	}

	/// <summary>
	/// Builds a declared module radio from a discovered plugin's advertised metadata.
	/// </summary>
	private static RadioRuntimeDefinition CreateDiscoveredModuleRadio(AudioProcessorCoordinator.DiscoveredRadioModule module)
	{
		ArgumentNullException.ThrowIfNull(module);

		// talk_permit defaults to false (§3.7.8): without an RM reporting Ready, TX uses a fixed
		// lead. A radio that needs a talk-permit opts in via config once its RM reports readiness.
		var config = new RadioModuleInstanceConfig(
			new KeyingConfig(SelectPreferredKeying(module.Capabilities), null, 120, 60, false),
			new DetectConfig(SelectPreferredDetect(module.Capabilities), null),
			new DeviceBindingConfig($"radio-{module.TypeId}"),
			new JsonObject());

		return CreateRadioDefinition(
			new RadioId(module.TypeId),
			module.TypeId,
			module.DisplayName,
			RadioRuntimeKind.Module,
			module.Capabilities,
			module.ConfigSchema,
			config);
	}

	private static RadioRuntimeDefinition? CreatePersistedRadio(AudioProcessorCoordinator.PersistedRadioDefinition definition, IReadOnlyList<AudioProcessorCoordinator.DiscoveredRadioModule> discoveredModules, Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(discoveredModules);
		ArgumentNullException.ThrowIfNull(log);

		if (string.IsNullOrWhiteSpace(definition.RadioId) || string.IsNullOrWhiteSpace(definition.TypeId))
		{
			log("config", "Skipped persisted radio definition because radio id or type id was missing.");
			return null;
		}

		var displayName = string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.RadioId : definition.DisplayName;
		if (string.Equals(definition.Kind, RadioRuntimeKind.Resource.ToString(), StringComparison.OrdinalIgnoreCase)
			|| string.Equals(definition.TypeId, "4w_resource", StringComparison.OrdinalIgnoreCase))
		{
			return CreateResourceRadio(definition.RadioId, definition.TypeId, displayName);
		}

		var discoveredModule = discoveredModules.FirstOrDefault(module => string.Equals(module.TypeId, definition.TypeId, StringComparison.OrdinalIgnoreCase));
		if (discoveredModule is null)
		{
			log("config", $"Persisted radio '{definition.RadioId}' type '{definition.TypeId}' has no loaded plugin; skipping it so the AP reports only discovered modules.");
			return null;
		}

		return CreateRadioDefinition(
			new RadioId(definition.RadioId),
			discoveredModule.TypeId,
			displayName,
			RadioRuntimeKind.Module,
			discoveredModule.Capabilities,
			discoveredModule.ConfigSchema,
			CreateInstanceConfig(definition, discoveredModule.Capabilities));
	}

	private static RadioModuleInstanceConfig CreateInstanceConfig(AudioProcessorCoordinator.PersistedRadioDefinition definition, RadioCapabilities capabilities)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(capabilities);

		var settings = DeserializeSettings(definition.InstanceConfigJson);
		return new RadioModuleInstanceConfig(
			new KeyingConfig(SelectPreferredKeying(capabilities), null, 120, 60, false),
			new DetectConfig(SelectPreferredDetect(capabilities), null),
			new DeviceBindingConfig($"radio-{definition.RadioId}"),
			settings);
	}

	private static JsonObject DeserializeSettings(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return [];
		}

		try
		{
			return JsonNode.Parse(json) as JsonObject ?? [];
		}
		catch (JsonException)
		{
			return [];
		}
	}

	private static KeyingMethod SelectPreferredKeying(RadioCapabilities capabilities)
	{
		return capabilities.Keying.Contains(KeyingMethod.Rm)
			? KeyingMethod.Rm
			: capabilities.Keying.FirstOrDefault(KeyingMethod.Relay);
	}

	private static DetectMethod SelectPreferredDetect(RadioCapabilities capabilities)
	{
		return capabilities.Detect.Contains(DetectMethod.Rm)
			? DetectMethod.Rm
			: capabilities.Detect.FirstOrDefault(DetectMethod.Vox);
	}

	/// <summary>
	/// Creates a built-in radio resource that uses AP relay keying and VOX detection only.
	/// </summary>
	private static RadioRuntimeDefinition CreateResourceRadio(string id, string typeId, string displayName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
		ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

		var capabilities = new RadioCapabilities(
			Keying: [KeyingMethod.Relay],
			Detect: [DetectMethod.Vox],
			ProvidesAudio: false,
			Controls: []);

		return CreateRadioDefinition(
			new RadioId(id),
			typeId,
			displayName,
			RadioRuntimeKind.Resource,
			capabilities,
			CreateResourceSettingsSchema(),
			new RadioModuleInstanceConfig(
				new KeyingConfig(KeyingMethod.Relay, new RelayBinding("RS-1", 1), 80, 40, false),
				new DetectConfig(DetectMethod.Vox, new VoxConfig(-45d, 20, 250)),
				new DeviceBindingConfig($"radio-{id}"),
				new JsonObject()));
	}

	private static RadioRuntimeDefinition CreateRadioDefinition(
		RadioId id,
		string typeId,
		string displayName,
		RadioRuntimeKind kind,
		RadioCapabilities capabilities,
		string settingsSchema,
		RadioModuleInstanceConfig config)
	{
		ArgumentNullException.ThrowIfNull(id);
		ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
		ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
		ArgumentNullException.ThrowIfNull(capabilities);
		ArgumentException.ThrowIfNullOrWhiteSpace(settingsSchema);
		ArgumentNullException.ThrowIfNull(config);

		var instanceSchema = RadioModuleSchemaBuilder.BuildInstanceSchema(capabilities, settingsSchema).ToJsonString();
		return new RadioRuntimeDefinition(id, typeId, displayName, kind, capabilities, settingsSchema, instanceSchema, config);
	}

	private static string CreateResourceSettingsSchema()
	{
		return """
		{
		  "$schema": "https://json-schema.org/draft/2020-12/schema",
		  "type": "object",
		  "properties": {},
		  "additionalProperties": false
		}
		""";
	}

}

internal enum RadioRuntimeKind
{
	Resource,
	Module,
	AdvancedModule
}

internal sealed record RadioRuntimeDefinition(
	RadioId Id,
	string TypeId,
	string DisplayName,
	RadioRuntimeKind Kind,
	RadioCapabilities Capabilities,
	string SettingsSchema,
	string InstanceSchema,
	RadioModuleInstanceConfig Config);

internal sealed class AudioProcessorRoutingState
{
	private RoutingSnapshot _currentSnapshot;

	private AudioProcessorRoutingState(RoutingSnapshot currentSnapshot)
	{
		_currentSnapshot = currentSnapshot;
	}

	public RoutingSnapshot CurrentSnapshot => _currentSnapshot;

	public static AudioProcessorRoutingState CreateDefault(IEnumerable<RadioId> radioIds, string speakerDeviceId)
	{
		ArgumentNullException.ThrowIfNull(radioIds);
		ArgumentException.ThrowIfNullOrWhiteSpace(speakerDeviceId);

		var crosspoints = radioIds
			.Select(static radioId => new RoutingCrosspoint(SourceEndpoint.OperatorMic, SinkEndpoint.ForRadioTx(radioId), 1.0m, false))
			.ToArray();

		return new AudioProcessorRoutingState(new RoutingSnapshot(new ReadOnlyCollection<RoutingCrosspoint>(crosspoints), SinkEndpoint.ForSpeaker(speakerDeviceId), null));
	}

	public void SetSpeakerSink(string deviceId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

		_currentSnapshot = _currentSnapshot with { SpeakerSink = SinkEndpoint.ForSpeaker(deviceId) };
	}

	public void SetOperatorMicTarget(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);

		_currentSnapshot = _currentSnapshot with { ActiveOperatorTarget = radioId };
	}

	public void ClearOperatorMicTarget(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);

		if (_currentSnapshot.ActiveOperatorTarget == radioId)
		{
			_currentSnapshot = _currentSnapshot with { ActiveOperatorTarget = null };
		}
	}
}

internal enum TxStatePhase
{
	Idle,
	Keying,
	Transmitting,
	Tail
}

internal sealed record RadioTxState(
	RadioId RadioId,
	RadioRuntimeKind Kind,
	RadioModuleInstanceConfig Config,
	TxStatePhase State,
	bool IsKeyAsserted,
	bool IsTalkPermitReady,
	DateTimeOffset LastTransitionUtc)
{
	public string KeyingMethodLabel => Config.Keying.Method == KeyingMethod.Relay ? "relay" : "rm";

	public static RadioTxState Create(RadioRuntimeDefinition radio)
	{
		ArgumentNullException.ThrowIfNull(radio);
		return new RadioTxState(
			radio.Id,
			radio.Kind,
			radio.Config,
			TxStatePhase.Idle,
			false,
			false,
			DateTimeOffset.UtcNow);
	}
}

internal sealed class AudioProcessorTopicFactory
{
	private const string RootTopic = "myforce/ap";

	private const string ConsoleRootTopic = "myforce/console";

	private const string SystemRootTopic = "myforce/sys";

	private const string ModuleRootTopic = "myforce/module";

	public string AllCommandsTopicFilter => $"{RootTopic}/cmd/#";

	public string AllModuleCommandsTopicFilter => $"{ModuleRootTopic}/+/cmd/#";

	public string ConsolePttCommandTopicFilter => $"{ConsoleRootTopic}/+/cmd/ptt";

	public string SystemPluginsTopic => $"{SystemRootTopic}/plugins";

	public string SystemDefinitionTopic => $"{SystemRootTopic}/definition";

	public string ConsoleTxTopic => $"{ConsoleRootTopic}/tx";

	public string MediaModuleId => "media.internet-radio";

	public string AudioModuleId => "audio.processor";

	public string MediaPlayCommandTopic => $"{ModuleRootTopic}/{MediaModuleId}/cmd/play";

	public string MediaStopCommandTopic => $"{ModuleRootTopic}/{MediaModuleId}/cmd/stop";

	public string MediaGainCommandTopic => $"{ModuleRootTopic}/{MediaModuleId}/cmd/gain";

	public string AudioOutputCommandTopic => $"{ModuleRootTopic}/{AudioModuleId}/cmd/output-speaker";

	public string AudioFrameworkSpecStateTopic => $"{ModuleRootTopic}/{AudioModuleId}/state";

	public static bool TryParseModuleCommandTopic(string topic, out string moduleId, out string commandName)
	{
		moduleId = string.Empty;
		commandName = string.Empty;

		if (string.IsNullOrWhiteSpace(topic))
		{
			return false;
		}

		var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 5
			|| !string.Equals(parts[0], "myforce", StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(parts[1], "module", StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(parts[3], "cmd", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		moduleId = parts[2];
		commandName = parts[4];
		return true;
	}

	public string ModuleRegistryTopic(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return $"{ModuleRootTopic}/{radioId.Value}/registry";
	}

	public string ModuleConfigTopic(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return $"{ModuleRootTopic}/{radioId.Value}/config";
	}

	public string ModuleStatusTopic(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return $"{ModuleRootTopic}/{radioId.Value}/status";
	}

	public string ModuleStateTopic(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return $"{ModuleRootTopic}/{radioId.Value}/state";
	}

	public string ManualPttRequestTopic => $"{RootTopic}/cmd/manual-ptt";

	public string ChannelGainCommandTopic => $"{RootTopic}/cmd/channel-gain";

	public string ChannelMuteCommandTopic => $"{RootTopic}/cmd/channel-mute";

	public string AudioOutputConfigCommandTopic => $"{RootTopic}/cmd/audio-output-config";

	public string OutputSpeakerCommandTopic => $"{RootTopic}/cmd/output-speaker";

	public string InternetRadioPlayCommandTopic => $"{RootTopic}/cmd/internet-radio/play";

	public string InternetRadioStopCommandTopic => $"{RootTopic}/cmd/internet-radio/stop";

	public string AudioFrameworkTopic => $"{RootTopic}/state/audio-framework";

	public string AudioMixerStateTopic => $"{RootTopic}/state/audio-mixer";

	public string InternetRadioStateTopic => $"{RootTopic}/state/internet-radio";

	public string RadioRuntimeTopic => $"{RootTopic}/state/radios";

	public string RoutingStateTopic => $"{RootTopic}/state/routing";

	public string ServiceRegistryTopic => $"{RootTopic}/registry/service";

	public string ServiceStatusTopic => $"{RootTopic}/status/service";
}

internal sealed record RadioId(string Value)
{
	public override string ToString() => Value;
}

internal sealed record BridgeId(string Value)
{
	public override string ToString() => Value;
}

internal sealed record AudioDeviceId(string Value)
{
	public override string ToString() => Value;
}

internal sealed record AudioBusId(string Value)
{
	public override string ToString() => Value;
}

internal sealed record AudioChannelId(string Value)
{
	public static AudioChannelId OperatorMic { get; } = new("operator-mic");

	public static AudioChannelId Entertainment { get; } = new("entertainment");

	public static AudioChannelId SpeakerMonitor { get; } = new("speaker-monitor");

	public static AudioChannelId RecorderFeed { get; } = new("recorder-feed");

	public static AudioChannelId ForRadioRx(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return new AudioChannelId($"radio-{radioId.Value}-rx");
	}

	public static AudioChannelId ForRadioTx(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return new AudioChannelId($"radio-{radioId.Value}-tx");
	}

	public override string ToString() => Value;
}

internal sealed record BridgeDefinition(BridgeId Id, ReadOnlyCollection<RadioId> Members);

internal sealed record AudioDevice(AudioDeviceId Id, string DisplayName, string Role, bool InputEnabled, bool OutputEnabled);

internal sealed record AudioBus(AudioBusId Id, string DisplayName, string Direction, ReadOnlyCollection<string> ChannelIds);

internal sealed record AudioChannelStrip(AudioChannelId Id, string DisplayName, string SignalPath, string DeviceRole, decimal DefaultGain, bool DefaultMuted, bool CanTransmit);

internal sealed record AudioMixerChannelState(AudioChannelId Id, decimal Gain, bool Muted, bool Active);

internal sealed record AudioMixerSnapshot(ReadOnlyCollection<AudioMixerChannelState> Channels);

internal sealed class AudioFrameworkCatalog
{
	public const string DefaultSpeakerDeviceId = "default-speaker";

	public const string SystemDefaultSpeakerDisplayName = "System Default Output";

	private static readonly Regex AlsaPlaybackDeviceRegex = new(
		@"^card\s+(?<cardNumber>\d+):\s+(?<cardId>[^\s\[]+)\s+\[(?<cardName>[^\]]+)\],\s+device\s+(?<deviceNumber>\d+):\s+(?<deviceId>[^\[]+)\[(?<deviceName>[^\]]+)\]",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public AudioFrameworkCatalog(
		IReadOnlyList<AudioDevice> devices,
		IReadOnlyList<AudioBus> buses,
		IReadOnlyList<AudioChannelStrip> channelStrips)
	{
		ArgumentNullException.ThrowIfNull(devices);
		ArgumentNullException.ThrowIfNull(buses);
		ArgumentNullException.ThrowIfNull(channelStrips);

		Devices = devices;
		Buses = buses;
		ChannelStrips = channelStrips;
	}

	public IReadOnlyList<AudioDevice> Devices { get; }

	public IReadOnlyList<AudioBus> Buses { get; }

	public IReadOnlyList<AudioChannelStrip> ChannelStrips { get; }

	public static AudioFrameworkCatalog CreateDefault(IEnumerable<RadioId> radioIds, IReadOnlyList<AudioDevice>? playbackDevices)
	{
		ArgumentNullException.ThrowIfNull(radioIds);

		var radioIdList = radioIds.ToArray();
		var devices = new List<AudioDevice>
		{
			new(new AudioDeviceId("operator-console"), "Operator Console", "operator", true, true),
			new(new AudioDeviceId("voice-recorder"), "Voice Recorder", "recorder", false, true)
		};

		if (playbackDevices is not null && playbackDevices.Count > 0)
		{
			devices.AddRange(playbackDevices);
		}
		else
		{
			devices.Add(new AudioDevice(new AudioDeviceId(DefaultSpeakerDeviceId), SystemDefaultSpeakerDisplayName, "speaker", false, true));
		}

		devices.AddRange(radioIdList.Select(static radioId =>
			new AudioDevice(new AudioDeviceId($"radio-{radioId.Value}"), $"Radio {radioId.Value}", "radio", true, true)));

		var channels = new List<AudioChannelStrip>
		{
			new(AudioChannelId.OperatorMic, "Operator Mic", "operator-mic -> tx-bus", "operator", 1.0m, false, true),
			new(AudioChannelId.Entertainment, "Entertainment", "entertainment -> speaker", "entertainment", 1.0m, false, false),
			new(AudioChannelId.SpeakerMonitor, "Speaker Monitor", "monitor-bus -> speaker", "speaker", 1.0m, false, false),
			new(AudioChannelId.RecorderFeed, "Recorder Feed", "mix-bus -> recorder", "recorder", 1.0m, false, false)
		};

		channels.AddRange(radioIdList.Select(static radioId =>
			new AudioChannelStrip(
				AudioChannelId.ForRadioRx(radioId),
				$"{radioId.Value.ToUpperInvariant()} RX",
				$"radio-{radioId.Value} -> monitor-bus",
				"radio",
				1.0m,
				false,
				false)));

		channels.AddRange(radioIdList.Select(static radioId =>
			new AudioChannelStrip(
				AudioChannelId.ForRadioTx(radioId),
				$"{radioId.Value.ToUpperInvariant()} TX",
				$"tx-bus -> radio-{radioId.Value}",
				"radio",
				1.0m,
				false,
				true)));

		var monitorBusChannels = channels
			.Where(static channel => channel.Id == AudioChannelId.SpeakerMonitor || channel.Id == AudioChannelId.Entertainment || channel.Id.Value.EndsWith("-rx", StringComparison.Ordinal))
			.Select(static channel => channel.Id.Value)
			.ToArray();

		var transmitBusChannels = channels
			.Where(static channel => channel.Id == AudioChannelId.OperatorMic || channel.Id.Value.EndsWith("-tx", StringComparison.Ordinal))
			.Select(static channel => channel.Id.Value)
			.ToArray();

		var buses = new List<AudioBus>
		{
			new(new AudioBusId("monitor-bus"), "Monitor Bus", "output", new ReadOnlyCollection<string>(monitorBusChannels)),
			new(new AudioBusId("tx-bus"), "Transmit Bus", "duplex", new ReadOnlyCollection<string>(transmitBusChannels)),
			new(new AudioBusId("record-bus"), "Record Bus", "output", new ReadOnlyCollection<string>([AudioChannelId.RecorderFeed.Value]))
		};

		return new AudioFrameworkCatalog(
			new ReadOnlyCollection<AudioDevice>(devices),
			new ReadOnlyCollection<AudioBus>(buses),
			new ReadOnlyCollection<AudioChannelStrip>(channels));
	}

	/// <summary>
	/// Discovers Linux playback sinks from PipeWire or PulseAudio so the AP can expose real output devices to the UI.
	/// </summary>
	public static IReadOnlyList<AudioDevice> DiscoverPlaybackDevices()
	{
		if (!OperatingSystem.IsLinux())
		{
			return Array.Empty<AudioDevice>();
		}

		try
		{
			var sinkJson = TryRunProcess("pactl", "-f json list sinks");
			if (!string.IsNullOrWhiteSpace(sinkJson))
			{
				var devicesFromJson = ParsePlaybackDevicesFromJson(sinkJson);
				if (devicesFromJson.Count > 0)
				{
					AudioProcessorLog.Write("discovery", $"Discovered {devicesFromJson.Count} Linux playback device(s) via pactl JSON sinks.");
					return devicesFromJson;
				}
			}

			var sinkShortList = TryRunProcess("pactl", "list short sinks");
			if (!string.IsNullOrWhiteSpace(sinkShortList))
			{
				var devicesFromShortList = ParsePlaybackDevicesFromShortList(sinkShortList);
				if (devicesFromShortList.Count > 0)
				{
					AudioProcessorLog.Write("discovery", $"Discovered {devicesFromShortList.Count} Linux playback device(s) via pactl short sinks.");
					return devicesFromShortList;
				}
			}

			var alsaHardwareList = TryRunProcess("aplay", "-l");
			if (!string.IsNullOrWhiteSpace(alsaHardwareList))
			{
				var devicesFromAlsa = ParsePlaybackDevicesFromAlsaHardwareList(alsaHardwareList);
				if (devicesFromAlsa.Count > 0)
				{
					AudioProcessorLog.Write("discovery", $"Discovered {devicesFromAlsa.Count} Linux playback device(s) via ALSA hardware enumeration.");
					return devicesFromAlsa;
				}
			}

			AudioProcessorLog.Write("discovery", "No Linux playback devices were discovered. Falling back to the synthetic system default output.");
			return Array.Empty<AudioDevice>();
		}
		catch (JsonException)
		{
			AudioProcessorLog.Write("discovery", "Linux playback discovery failed while parsing pactl JSON sink output.");
			return Array.Empty<AudioDevice>();
		}
		catch (InvalidOperationException)
		{
			AudioProcessorLog.Write("discovery", "Linux playback discovery could not launch the enumeration process.");
			return Array.Empty<AudioDevice>();
		}
		catch (System.ComponentModel.Win32Exception)
		{
			AudioProcessorLog.Write("discovery", "Linux playback discovery requires pactl or aplay to be installed and accessible on PATH.");
			return Array.Empty<AudioDevice>();
		}
	}

	private static string? TryRunProcess(string fileName, string arguments)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = fileName,
			Arguments = arguments,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		LinuxRuntimeEnvironment.Apply(startInfo);
		using var process = new Process { StartInfo = startInfo };

		if (!process.Start())
		{
			return null;
		}

		var output = process.StandardOutput.ReadToEnd();
		process.WaitForExit(3000);
		return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)
			? output
			: null;
	}

	private static IReadOnlyList<AudioDevice> ParsePlaybackDevicesFromJson(string output)
	{
		using var json = JsonDocument.Parse(output);
		if (json.RootElement.ValueKind != JsonValueKind.Array)
		{
			return Array.Empty<AudioDevice>();
		}

		var devices = new List<AudioDevice>();
		foreach (var sink in json.RootElement.EnumerateArray())
		{
			if (!sink.TryGetProperty("name", out var nameElement))
			{
				continue;
			}

			var deviceId = nameElement.GetString();
			if (string.IsNullOrWhiteSpace(deviceId))
			{
				continue;
			}

			var displayName = deviceId;
			if (sink.TryGetProperty("description", out var descriptionElement)
				&& descriptionElement.ValueKind == JsonValueKind.String
				&& !string.IsNullOrWhiteSpace(descriptionElement.GetString()))
			{
				displayName = descriptionElement.GetString()!;
			}
			else if (sink.TryGetProperty("properties", out var propertiesElement)
				&& propertiesElement.ValueKind == JsonValueKind.Object
				&& propertiesElement.TryGetProperty("device.description", out var deviceDescriptionElement)
				&& deviceDescriptionElement.ValueKind == JsonValueKind.String
				&& !string.IsNullOrWhiteSpace(deviceDescriptionElement.GetString()))
			{
				displayName = deviceDescriptionElement.GetString()!;
			}

			devices.Add(new AudioDevice(new AudioDeviceId(deviceId), displayName, "speaker", false, true));
		}

		return CreateOrderedPlaybackDeviceList(devices);
	}

	private static IReadOnlyList<AudioDevice> ParsePlaybackDevicesFromShortList(string output)
	{
		var devices = new List<AudioDevice>();
		using var reader = new StringReader(output);
		string? line;

		while ((line = reader.ReadLine()) is not null)
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			var columns = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (columns.Length < 2)
			{
				continue;
			}

			var deviceId = columns[1];
			if (string.IsNullOrWhiteSpace(deviceId))
			{
				continue;
			}

			var displayName = columns.Length >= 5 && !string.IsNullOrWhiteSpace(columns[4])
				? columns[4]
				: deviceId;
			devices.Add(new AudioDevice(new AudioDeviceId(deviceId), displayName, "speaker", false, true));
		}

		return CreateOrderedPlaybackDeviceList(devices);
	}

	private static IReadOnlyList<AudioDevice> CreateOrderedPlaybackDeviceList(IEnumerable<AudioDevice> devices)
	{
		return new ReadOnlyCollection<AudioDevice>(devices
			.GroupBy(device => device.Id.Value, StringComparer.OrdinalIgnoreCase)
			.Select(group => group.First())
			.OrderBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ToArray());
	}

	private static IReadOnlyList<AudioDevice> ParsePlaybackDevicesFromAlsaHardwareList(string output)
	{
		var devices = new List<AudioDevice>();
		using var reader = new StringReader(output);
		string? line;

		while ((line = reader.ReadLine()) is not null)
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			var match = AlsaPlaybackDeviceRegex.Match(line);
			if (!match.Success)
			{
				continue;
			}

			var cardNumber = match.Groups["cardNumber"].Value;
			var deviceNumber = match.Groups["deviceNumber"].Value;
			var cardName = match.Groups["cardName"].Value.Trim();
			var deviceName = match.Groups["deviceName"].Value.Trim();
			var deviceId = $"alsa:hw:{cardNumber},{deviceNumber}";
			var displayName = string.IsNullOrWhiteSpace(deviceName)
				? cardName
				: $"{cardName} - {deviceName}";

			devices.Add(new AudioDevice(new AudioDeviceId(deviceId), displayName, "speaker", false, true));
		}

		return CreateOrderedPlaybackDeviceList(devices);
	}
}

internal sealed class AudioMixerState
{
	private readonly Dictionary<string, AudioMixerChannelState> _channels;

	private AudioMixerState(Dictionary<string, AudioMixerChannelState> channels)
	{
		_channels = channels;
	}

	public AudioMixerSnapshot CurrentSnapshot => new(new ReadOnlyCollection<AudioMixerChannelState>(_channels.Values.OrderBy(static channel => channel.Id.Value, StringComparer.Ordinal).ToArray()));

	public static AudioMixerState CreateDefault(IEnumerable<AudioChannelStrip> channelStrips)
	{
		ArgumentNullException.ThrowIfNull(channelStrips);

		var channels = channelStrips.ToDictionary(
			static channel => channel.Id.Value,
			static channel => new AudioMixerChannelState(channel.Id, NormalizeGain(channel.DefaultGain), channel.DefaultMuted, false),
			StringComparer.OrdinalIgnoreCase);

		return new AudioMixerState(channels);
	}

	public void SetGain(string channelId, decimal gain)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

		var state = GetChannelState(channelId);
		_channels[channelId] = state with { Gain = NormalizeGain(gain) };
	}

	public void SetMuted(string channelId, bool isMuted)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

		var state = GetChannelState(channelId);
		_channels[channelId] = state with { Muted = isMuted };
	}

	public void SetChannelActive(AudioChannelId channelId, bool isActive)
	{
		ArgumentNullException.ThrowIfNull(channelId);

		var state = GetChannelState(channelId.Value);
		_channels[channelId.Value] = state with { Active = isActive };
	}

	public void SetTransmitTarget(RadioId radioId, bool isActive)
	{
		ArgumentNullException.ThrowIfNull(radioId);

		foreach (var channel in _channels.Values.Where(static channel => channel.Id.Value.EndsWith("-tx", StringComparison.Ordinal)).ToArray())
		{
			_channels[channel.Id.Value] = channel with { Active = false };
		}

		if (!isActive)
		{
			return;
		}

		var channelId = AudioChannelId.ForRadioTx(radioId);
		var state = GetChannelState(channelId.Value);
		_channels[channelId.Value] = state with { Active = true };
	}

	private AudioMixerChannelState GetChannelState(string channelId)
	{
		if (_channels.TryGetValue(channelId, out var state))
		{
			return state;
		}

		throw new InvalidOperationException($"Unknown audio channel '{channelId}'.");
	}

	private static decimal NormalizeGain(decimal gain)
	{
		return decimal.Clamp(gain, 0m, 2m);
	}
}

internal sealed class AudioMatrixEngine
{
	private AudioMixerSnapshot _mixerSnapshot;
	private RoutingSnapshot _routingSnapshot;

	public AudioMatrixEngine(AudioMixerSnapshot mixerSnapshot, RoutingSnapshot routingSnapshot)
	{
		ArgumentNullException.ThrowIfNull(mixerSnapshot);
		ArgumentNullException.ThrowIfNull(routingSnapshot);
		_mixerSnapshot = mixerSnapshot;
		_routingSnapshot = routingSnapshot;
	}

	public void UpdateMixer(AudioMixerSnapshot mixerSnapshot)
	{
		ArgumentNullException.ThrowIfNull(mixerSnapshot);
		_mixerSnapshot = mixerSnapshot;
	}

	public void UpdateRouting(RoutingSnapshot routingSnapshot)
	{
		ArgumentNullException.ThrowIfNull(routingSnapshot);
		_routingSnapshot = routingSnapshot;
	}

	public void UpdateSnapshots(AudioMixerSnapshot mixerSnapshot, RoutingSnapshot routingSnapshot)
	{
		UpdateMixer(mixerSnapshot);
		UpdateRouting(routingSnapshot);
	}

	public int Mix(ReadOnlySpan<AudioMatrixInputFrame> inputs, Span<float> output)
	{
		output.Clear();
		if (inputs.IsEmpty || output.IsEmpty)
		{
			return 0;
		}

		var channels = _mixerSnapshot.Channels.ToDictionary(static channel => channel.Id.Value, StringComparer.OrdinalIgnoreCase);
		foreach (var input in inputs)
		{
			if (!channels.TryGetValue(input.ChannelId, out var channel) || channel.Muted)
			{
				continue;
			}

			var samples = input.Samples.Span;
			var sampleCount = Math.Min(samples.Length, output.Length);
			var gain = (float)channel.Gain;
			for (var index = 0; index < sampleCount; index++)
			{
				output[index] += samples[index] * gain;
			}
		}

		return output.Length;
	}

	public IReadOnlyList<RoutingCrosspoint> ActiveCrosspoints => _routingSnapshot.Crosspoints.Where(static crosspoint => crosspoint.Enabled).ToArray();
}

internal readonly record struct AudioMatrixInputFrame(string ChannelId, ReadOnlyMemory<float> Samples);

internal sealed record RoutingSnapshot(
	ReadOnlyCollection<RoutingCrosspoint> Crosspoints,
	SinkEndpoint SpeakerSink,
	RadioId? ActiveOperatorTarget);

internal sealed record RoutingCrosspoint(
	SourceEndpoint Source,
	SinkEndpoint Sink,
	decimal Gain,
	bool Enabled);

internal sealed record SourceEndpoint(string Kind, string? RadioId = null)
{
	public static SourceEndpoint OperatorMic { get; } = new("operator-mic");

	public static SourceEndpoint ForRadioRx(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return new SourceEndpoint("radio-rx", radioId.Value);
	}
}

internal sealed record SinkEndpoint(string Kind, string? RadioId = null)
{
	public static SinkEndpoint Speaker { get; } = new("speaker");

	public string? DeviceId => Kind == "speaker" ? RadioId : null;

	public static SinkEndpoint ForSpeaker(string deviceId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
		return new SinkEndpoint("speaker", deviceId);
	}

	public static SinkEndpoint ForRadioTx(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return new SinkEndpoint("radio-tx", radioId.Value);
	}
}

internal sealed record ManualPttRequest(RadioId RadioId, bool IsPressed, string? Origin, string? V, DateTimeOffset? Ts, string? MsgId, string? Auth, bool IsOverride = false);

/// <summary>
/// Output of <see cref="AudioProcessorCoordinator.BuildEngineSetup"/>: the fixed engine topology
/// plus the ordered backend device id lists and the RX source index per radio for VOX (§3.6.8).
/// </summary>
internal sealed record EngineSetupResult(
	EngineTopology Topology,
	IReadOnlyList<string> CaptureDeviceIds,
	IReadOnlyList<string> PlaybackDeviceIds,
	Dictionary<string, int> RxSourceIndexByRadio);

internal sealed class ConsolePttRequest
{
	public int V { get; init; }

	public DateTimeOffset Ts { get; init; }

	[JsonPropertyName("msg_id")]
	public string? MsgId { get; init; }

	public string? Auth { get; init; }

	public string Target { get; init; } = string.Empty;

	public string State { get; init; } = string.Empty;

	public string Origin { get; init; } = string.Empty;

	public bool? Override { get; init; }
}

internal sealed record MqttCommandEnvelope(string? MsgId, string? Auth);

internal sealed record AudioChannelGainCommand(string ChannelId, decimal Gain);

internal sealed record AudioChannelMuteCommand(string ChannelId, bool IsMuted);

internal sealed record AudioOutputConfigCommand(string DeviceId, string? CabinSpeakerPipeWireSinkName, string? HeadrestSpeakerPipeWireSinkName);

internal sealed record OutputSpeakerCommand(string DeviceId);

internal sealed record InternetRadioPlayCommand(string StreamUrl, string DisplayName, string Genre, string Language);

internal sealed record ModuleConfigCommandPayload(
	int V,
	DateTimeOffset Ts,
	[property: JsonPropertyName("msg_id")] string? MsgId,
	string? Auth,
	string Id,
	JsonObject Config,
	bool? Partial);

internal sealed record InternetRadioPlaybackState(bool IsPlaying, string? StreamUrl, string? DisplayName, string? Genre, string? Language, string Status, string Detail);

internal sealed record ServiceRegistryPayload(
	string ServiceId,
	string DisplayName,
	IReadOnlyList<RadioRegistryPayload> Radios,
	IReadOnlyList<string> RadioIds,
	IReadOnlyList<string> BridgeIds)
{
	public static ServiceRegistryPayload Create(AudioProcessorRegistry registry)
	{
		ArgumentNullException.ThrowIfNull(registry);

		return new ServiceRegistryPayload(
			ServiceId: "audio-processor",
			DisplayName: "Audio Processor",
			Radios: registry.Radios.Select(static radio => new RadioRegistryPayload(
				radio.Id.Value,
				radio.TypeId,
				radio.DisplayName,
				radio.Kind.ToString(),
				radio.Capabilities,
				radio.SettingsSchema,
				radio.InstanceSchema)).ToArray(),
			RadioIds: registry.RadioIds.Select(static radioId => radioId.Value).ToArray(),
			BridgeIds: registry.Bridges.Select(static bridge => bridge.Id.Value).ToArray());
	}
}

internal sealed record MqttEnvelopePayload(int V, DateTimeOffset Ts, [property: JsonPropertyName("msg_id")] string? MsgId = null, string? Auth = null)
{
	public static MqttEnvelopePayload Create(string? msgId = null, string? auth = null) => new(1, DateTimeOffset.UtcNow, msgId, auth);
}

internal sealed record CommandAckPayload(
	int V,
	DateTimeOffset Ts,
	[property: JsonPropertyName("msg_id")] string MsgId,
	string Status,
	IReadOnlyList<CommandAckErrorPayload>? Errors);

internal sealed record CommandAckErrorPayload(string? Field, string Code, string Message);

internal sealed record SystemPluginsPayload(int V, DateTimeOffset Ts, IReadOnlyList<SystemPluginTypePayload> Types)
{
	public static SystemPluginsPayload Create(IReadOnlyList<AudioProcessorCoordinator.DiscoveredRadioModule> discoveredModules)
	{
		ArgumentNullException.ThrowIfNull(discoveredModules);

		var types = discoveredModules
			.Select(static module => new SystemPluginTypePayload(
				TypeId: module.TypeId,
				DisplayName: module.DisplayName,
				Kind: "radio_module",
				Version: module.Version))
			.Append(new SystemPluginTypePayload("4w_resource", "4-Wire Resource", "radio_resource", "core"));

		return new SystemPluginsPayload(1, DateTimeOffset.UtcNow, types.ToArray());
	}
}

internal sealed record SystemPluginTypePayload(
	[property: JsonPropertyName("type_id")] string TypeId,
	[property: JsonPropertyName("display_name")] string DisplayName,
	string Kind,
	string Version);

internal sealed record SystemDefinitionPayload(
	int V,
	DateTimeOffset Ts,
	IReadOnlyList<SystemDefinitionModulePayload> Modules,
	IReadOnlyList<SystemDefinitionBridgePayload> Bridges,
	IReadOnlyList<SystemDefinitionConsolePayload> Consoles)
{
	public static SystemDefinitionPayload Create(AudioProcessorRegistry registry)
	{
		ArgumentNullException.ThrowIfNull(registry);

		return new SystemDefinitionPayload(
			1,
			DateTimeOffset.UtcNow,
			registry.Radios.Select(static radio => new SystemDefinitionModulePayload(
				Id: radio.Id.Value,
				TypeId: radio.TypeId,
				Alias: radio.DisplayName,
				Category: "radio",
				Required: radio.Kind == RadioRuntimeKind.Resource)).ToArray(),
			registry.Bridges.Select(static bridge => new SystemDefinitionBridgePayload(bridge.Id.Value, bridge.Id.Value)).ToArray(),
			[new SystemDefinitionConsolePayload("vip", "Vehicle Interface")]);
	}
}

internal sealed record SystemDefinitionModulePayload(
	string Id,
	[property: JsonPropertyName("type_id")] string TypeId,
	string Alias,
	string Category,
	bool Required);

internal sealed record SystemDefinitionBridgePayload(string Id, string Alias);

internal sealed record SystemDefinitionConsolePayload(string Id, string Alias);

internal sealed record ModuleRegistrySpecPayload(
	int V,
	DateTimeOffset Ts,
	string Id,
	[property: JsonPropertyName("type_id")] string TypeId,
	string Kind,
	string Category,
	bool Removable,
	[property: JsonPropertyName("config_schema")] JsonObject ConfigSchema,
	RadioCapabilities Capabilities)
{
	public static ModuleRegistrySpecPayload Create(RadioRuntimeDefinition radio)
	{
		ArgumentNullException.ThrowIfNull(radio);

		var configSchema = JsonNode.Parse(radio.InstanceSchema) as JsonObject ?? [];
		return new ModuleRegistrySpecPayload(
			1,
			DateTimeOffset.UtcNow,
			radio.Id.Value,
			radio.TypeId,
			radio.Kind == RadioRuntimeKind.Resource ? "radio_resource" : "radio_module",
			"radio",
			radio.Kind != RadioRuntimeKind.Resource,
			configSchema,
			radio.Capabilities);
	}
}

internal sealed record ModuleConfigSpecPayload(int V, DateTimeOffset Ts, string Id, RadioInstanceConfigPayload Config)
{
	public static ModuleConfigSpecPayload Create(RadioRuntimeDefinition radio)
	{
		ArgumentNullException.ThrowIfNull(radio);
		return new ModuleConfigSpecPayload(1, DateTimeOffset.UtcNow, radio.Id.Value, RadioInstanceConfigPayload.Create(radio.Config));
	}
}

internal sealed record ModuleStatusSpecPayload(int V, DateTimeOffset Ts, string Id, bool Online, string Health, string? Reason)
{
	public static ModuleStatusSpecPayload CreateOnline(RadioRuntimeDefinition radio)
	{
		ArgumentNullException.ThrowIfNull(radio);
		return new ModuleStatusSpecPayload(1, DateTimeOffset.UtcNow, radio.Id.Value, true, "available", null);
	}

	/// <summary>
	/// Device-health status (§3.6.10): the AP process stays online, but a radio whose bound device
	/// is absent reports Unavailable with a device_absent reason (§5.8.4).
	/// </summary>
	public static ModuleStatusSpecPayload CreateForHealth(RadioRuntimeDefinition radio, bool isPresent)
	{
		ArgumentNullException.ThrowIfNull(radio);
		return new ModuleStatusSpecPayload(
			1,
			DateTimeOffset.UtcNow,
			radio.Id.Value,
			Online: true,
			Health: isPresent ? "available" : "unavailable",
			Reason: isPresent ? null : "device_absent");
	}
}

internal sealed record RadioRegistryPayload(
	string RadioId,
	string TypeId,
	string DisplayName,
	string Kind,
	RadioCapabilities Capabilities,
	string ConfigSchema,
	string InstanceSchema);

internal sealed record RadioRuntimePayload(IReadOnlyList<RadioRuntimeStatePayload> Radios)
{
	public static RadioRuntimePayload Create(AudioProcessorRegistry registry, TxStateMachine txController)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(txController);

		return new RadioRuntimePayload(
			registry.Radios.Select(radio =>
			{
				var state = txController.GetState(radio.Id);
				return new RadioRuntimeStatePayload(
					RadioId: radio.Id.Value,
					TypeId: radio.TypeId,
					DisplayName: radio.DisplayName,
					Kind: radio.Kind.ToString(),
					Capabilities: radio.Capabilities,
					ConfigSchema: radio.SettingsSchema,
					InstanceSchema: radio.InstanceSchema,
					Config: RadioInstanceConfigPayload.Create(radio.Config),
					TxState: RadioTxStatePayload.Create(state));
			}).ToArray());
	}
}

internal sealed record RadioRuntimeStatePayload(
	string RadioId,
	string TypeId,
	string DisplayName,
	string Kind,
	RadioCapabilities Capabilities,
	string ConfigSchema,
	string InstanceSchema,
	RadioInstanceConfigPayload Config,
	RadioTxStatePayload TxState);

internal sealed record RadioInstanceConfigPayload(
	KeyingConfigPayload Keying,
	DetectConfigPayload Detect,
	DeviceBindingPayload? Device,
	JsonObject Settings)
{
	public static RadioInstanceConfigPayload Create(RadioModuleInstanceConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);
		return new RadioInstanceConfigPayload(
			KeyingConfigPayload.Create(config.Keying),
			DetectConfigPayload.Create(config.Detect),
			config.Device is null ? null : new DeviceBindingPayload(config.Device.Soundcard),
			(JsonObject)config.Settings.DeepClone());
	}
}

internal sealed record KeyingConfigPayload(string Method, RelayBinding? Relay, int PttLeadMs, int PttTailMs, bool TalkPermit)
{
	public static KeyingConfigPayload Create(KeyingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);
		return new KeyingConfigPayload(config.Method.ToString().ToLowerInvariant(), config.Relay, config.PttLeadMs, config.PttTailMs, config.TalkPermit);
	}
}

internal sealed record DetectConfigPayload(string Method, VoxConfig? Vox)
{
	public static DetectConfigPayload Create(DetectConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);
		return new DetectConfigPayload(config.Method.ToString().ToLowerInvariant(), config.Vox);
	}
}

internal sealed record DeviceBindingPayload(string? Soundcard);

internal sealed record RadioTxStatePayload(
	string Phase,
	bool IsKeyAsserted,
	bool IsTalkPermitReady,
	string KeyingMethod,
	int PttLeadMs,
	int PttTailMs,
	DateTimeOffset LastTransitionUtc)
{
	public static RadioTxStatePayload Create(RadioTxState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return new RadioTxStatePayload(
			state.State.ToString(),
			state.IsKeyAsserted,
			state.IsTalkPermitReady,
			state.Config.Keying.Method.ToString().ToLowerInvariant(),
			state.Config.Keying.PttLeadMs,
			state.Config.Keying.PttTailMs,
			state.LastTransitionUtc);
	}
}

internal sealed record InternetRadioStatePayload(
	bool IsPlaying,
	string? StreamUrl,
	string? DisplayName,
	string? Genre,
	string? Language,
	string Status,
	string Detail)
{
	public static InternetRadioStatePayload Create(InternetRadioPlaybackState state)
	{
		ArgumentNullException.ThrowIfNull(state);

		return new InternetRadioStatePayload(
			state.IsPlaying,
			state.StreamUrl,
			state.DisplayName,
			state.Genre,
			state.Language,
			state.Status,
			state.Detail);
	}
}

internal sealed record ServiceStatusPayload(
	string ServiceId,
	AudioProcessorServiceState State,
	int RadioCount,
	int BridgeCount,
	string? ActiveManualTransmitRadioId)
{
	public static ServiceStatusPayload CreateRunning(string serviceId, int radioCount, int bridgeCount, string? activeManualTransmitRadioId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

		return new ServiceStatusPayload(serviceId, AudioProcessorServiceState.Running, radioCount, bridgeCount, activeManualTransmitRadioId);
	}

	public static ServiceStatusPayload CreateStopped(string serviceId, int radioCount, int bridgeCount, string? activeManualTransmitRadioId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

		return new ServiceStatusPayload(serviceId, AudioProcessorServiceState.Stopped, radioCount, bridgeCount, activeManualTransmitRadioId);
	}
}

internal sealed record RoutingStatePayload(
	string? ActiveOperatorTarget,
	string SpeakerDeviceId,
	IReadOnlyList<RoutingCrosspointPayload> Crosspoints)
{
	public static RoutingStatePayload Create(RoutingSnapshot snapshot, IAudioProcessorStoredConfig config)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(config);

		return new RoutingStatePayload(
			ActiveOperatorTarget: snapshot.ActiveOperatorTarget?.Value,
			SpeakerDeviceId: snapshot.SpeakerSink.DeviceId ?? AudioFrameworkCatalog.DefaultSpeakerDeviceId,
			Crosspoints: snapshot.Crosspoints
				.Select(static crosspoint => new RoutingCrosspointPayload(
					crosspoint.Source.Kind,
					crosspoint.Source.RadioId,
					crosspoint.Sink.Kind,
					crosspoint.Sink.RadioId,
					crosspoint.Gain,
					crosspoint.Enabled))
				.ToArray());
	}
}

internal sealed record AudioFrameworkPayload(
	string ServiceId,
	IReadOnlyList<AudioDevicePayload> Devices,
	IReadOnlyList<AudioBusPayload> Buses,
	IReadOnlyList<AudioChannelStripPayload> Channels)
{
	public static AudioFrameworkPayload Create(AudioFrameworkCatalog framework)
	{
		ArgumentNullException.ThrowIfNull(framework);

		return new AudioFrameworkPayload(
			ServiceId: "audio-processor",
			Devices: framework.Devices
				.Select(static device => new AudioDevicePayload(device.Id.Value, device.DisplayName, device.Role, device.InputEnabled, device.OutputEnabled))
				.ToArray(),
			Buses: framework.Buses
				.Select(static bus => new AudioBusPayload(bus.Id.Value, bus.DisplayName, bus.Direction, bus.ChannelIds))
				.ToArray(),
			Channels: framework.ChannelStrips
				.Select(static channel => new AudioChannelStripPayload(
					channel.Id.Value,
					channel.DisplayName,
					channel.SignalPath,
					channel.DeviceRole,
					channel.DefaultGain,
					channel.DefaultMuted,
					channel.CanTransmit))
				.ToArray());
	}
}

internal sealed record AudioMixerStatePayload(IReadOnlyList<AudioMixerChannelPayload> Channels)
{
	public static AudioMixerStatePayload Create(AudioMixerSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return new AudioMixerStatePayload(
			snapshot.Channels
				.Select(static channel => new AudioMixerChannelPayload(channel.Id.Value, channel.Gain, channel.Muted, channel.Active))
				.ToArray());
	}
}

internal sealed record AudioDevicePayload(string DeviceId, string DisplayName, string Role, bool InputEnabled, bool OutputEnabled);

internal sealed record AudioBusPayload(string BusId, string DisplayName, string Direction, IReadOnlyList<string> ChannelIds);

internal sealed record AudioChannelStripPayload(string ChannelId, string DisplayName, string SignalPath, string DeviceRole, decimal DefaultGain, bool DefaultMuted, bool CanTransmit);

internal sealed record AudioMixerChannelPayload(string ChannelId, decimal Gain, bool Muted, bool Active);

internal sealed record RoutingCrosspointPayload(
	string SourceKind,
	string? SourceRadioId,
	string SinkKind,
	string? SinkRadioId,
	decimal Gain,
	bool Enabled);

internal enum AudioProcessorServiceState
{
	Stopped = 0,

	Running = 1
}

internal static class AudioProcessorJson
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false,
		// Capability enums must serialize as lowercase strings so the UI reads keying/detect as
		// ["relay","rm"] / ["vox","rm"] per §3.7.4, not as numeric values. Scoped to these two
		// enums so other enums (e.g. service state) keep their existing numeric wire form.
		Converters =
		{
			new JsonStringEnumConverter<MyForce.Contracts.Radio.KeyingMethod>(JsonNamingPolicy.CamelCase),
			new JsonStringEnumConverter<MyForce.Contracts.Radio.DetectMethod>(JsonNamingPolicy.CamelCase)
		}
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
}

internal sealed class InternetRadioPlaybackController : IAsyncDisposable
{
	private const int MaxUnexpectedLinuxRestartAttempts = 3;

	private const int MaxProcessDiagnosticLines = 20;

	// PulseAudio/PipeWire application.name tag set on the ffplay stream so the AP can find its
	// sink-input deterministically (by name) to apply the entertainment mixer gain.
	internal const string EntertainmentSinkInputAppName = "myforce-entertainment";

	private static readonly TimeSpan UnexpectedLinuxRestartResetWindow = TimeSpan.FromSeconds(30);

	private readonly AudioProcessorConfigStore _configStore;

	private readonly HttpClient _httpClient;

	// Serializes play/stop so concurrent commands cannot spawn two players at once. The AP owns the
	// single entertainment source into the matrix (§3.5), so exactly one stream may be active.
	private readonly SemaphoreSlim _playbackGate = new(1, 1);

	private readonly ConcurrentQueue<string> _linuxPlayerDiagnostics = new();

	private Process? _externalPlayerProcess;

	private bool _isStoppingExternalPlayer;

	private IWavePlayer? _waveOut;

	private MediaFoundationReader? _reader;

	private InternetRadioPlayCommand? _activeCommand;

	private string? _activeBackend;

	private string _outputSpeakerDeviceId = AudioFrameworkCatalog.DefaultSpeakerDeviceId;

	private decimal _outputGain = 1.0m;

	private int _unexpectedLinuxRestartAttempts;

	private DateTimeOffset? _linuxPlaybackStartedAtUtc;

	public InternetRadioPlaybackController(AudioProcessorConfigStore configStore)
	{
		ArgumentNullException.ThrowIfNull(configStore);

		_configStore = configStore;
		_httpClient = new HttpClient();
		_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MyForce-AudioProcessor/1.0");
		CurrentState = new InternetRadioPlaybackState(false, null, null, null, null, "IDLE", "No internet radio stream selected.");
	}

	public InternetRadioPlaybackState CurrentState { get; private set; }

	public InternetRadioPlayCommand? GetStoredPlayCommand()
	{
		var commandJson = _configStore.StoredConfig.InternetRadioPlayCommandJson;
		if (string.IsNullOrWhiteSpace(commandJson))
		{
			return null;
		}

		try
		{
			return System.Text.Json.JsonSerializer.Deserialize<InternetRadioPlayCommand>(commandJson);
		}
		catch (JsonException)
		{
			AudioProcessorLog.Write("playback", "Stored internet radio restore payload was invalid JSON and has been cleared.");
			_configStore.StoredConfig.InternetRadioPlayCommandJson = string.Empty;
			return null;
		}
	}

	public void SetOutputSpeaker(string? deviceId)
	{
		if (string.IsNullOrWhiteSpace(deviceId))
		{
			_outputSpeakerDeviceId = AudioFrameworkCatalog.DefaultSpeakerDeviceId;
		}
		else
		{
			_outputSpeakerDeviceId = deviceId;
		}

		if (CurrentState.IsPlaying)
		{
			CurrentState = CurrentState with
			{
				Detail = $"Internet radio stream playing on {GetPlaybackBackendDescription()}."
			};
		}
	}

	/// <summary>
	/// Starts internet radio playback on the default output device using the provided stream metadata.
	/// </summary>
	public async Task PlayAsync(InternetRadioPlayCommand command, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(command);
		ArgumentException.ThrowIfNullOrWhiteSpace(command.StreamUrl);
		ArgumentException.ThrowIfNullOrWhiteSpace(command.DisplayName);

		// One stream at a time: a second concurrent play waits here, then the release below tears
		// down the prior player before this one starts, so two ffplay processes can never coexist.
		await _playbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (CanReuseActivePlayback(command))
			{
				_activeCommand = command;
				PersistActiveCommand(command);
				CurrentState = CurrentState with
				{
					IsPlaying = true,
					StreamUrl = command.StreamUrl,
					DisplayName = command.DisplayName,
					Genre = command.Genre,
					Language = command.Language,
					Status = "PLAYING",
					Detail = $"Internet radio stream playing on {GetPlaybackBackendDescription()}."
				};

				return;
			}

			await EnsureStreamReachableAsync(command.StreamUrl, cancellationToken).ConfigureAwait(false);

			ReleasePlaybackResources();

			if (OperatingSystem.IsWindows())
			{
				StartWindowsPlayback(command);
			}
			else if (OperatingSystem.IsLinux())
			{
				StartLinuxPlayback(command);
			}
			else
			{
				throw new PlatformNotSupportedException("Internet radio playback is currently supported on Windows and Linux only.");
			}

			_activeCommand = command;
			PersistActiveCommand(command);

			CurrentState = new InternetRadioPlaybackState(
				IsPlaying: true,
				StreamUrl: command.StreamUrl,
				DisplayName: command.DisplayName,
				Genre: command.Genre,
				Language: command.Language,
				Status: "PLAYING",
				Detail: $"Internet radio stream playing on {GetPlaybackBackendDescription()}.");
		}
		finally
		{
			_playbackGate.Release();
		}
	}

	/// <summary>
	/// Stops the current internet radio stream and releases playback resources.
	/// </summary>
	public void Stop()
	{
		// Take the same gate as PlayAsync so a stop cannot race a start and leave an orphan player.
		_playbackGate.Wait();
		try
		{
			ReleasePlaybackResources();
			_activeCommand = null;
			_configStore.StoredConfig.InternetRadioPlayCommandJson = string.Empty;
			_unexpectedLinuxRestartAttempts = 0;

			CurrentState = CurrentState with
			{
				IsPlaying = false,
				Status = "STOPPED",
				Detail = "Internet radio playback stopped."
			};
		}
		finally
		{
			_playbackGate.Release();
		}
	}

	/// <summary>
	/// Applies the AP entertainment mixer gain to the active internet-radio output path.
	/// </summary>
	public void SetOutputGain(string channelId, decimal gain)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

		if (!string.Equals(channelId, AudioChannelId.Entertainment.Value, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		_outputGain = decimal.Clamp(gain, 0m, 2m);
		ApplyCurrentOutputGain();
	}

	private void ApplyCurrentOutputGain()
	{
		if (_waveOut is not null)
		{
			_waveOut.Volume = (float)Math.Clamp(_outputGain / 2.0m, 0m, 1.0m);
			return;
		}

		if (OperatingSystem.IsLinux() && _externalPlayerProcess is not null && _activeCommand is not null)
		{
			// ffplay stays at its fixed launch volume; the operator's gain is applied to the stream's
			// PipeWire sink-input level feeding the master output (the entertainment mixer input).
			TryApplyLinuxEntertainmentGain();
			CurrentState = CurrentState with
			{
				Detail = $"Internet radio stream playing on {GetPlaybackBackendDescription()}. Entertainment gain applied via the PipeWire mixer."
			};
		}
	}

	/// <summary>
	/// Applies the entertainment gain to the running ffplay stream's PipeWire sink-input volume, so
	/// loudness is controlled by the AP mixer into the master output rather than by ffplay itself.
	/// Best-effort: a no-op if pactl or the sink-input is unavailable.
	/// </summary>
	private void TryApplyLinuxEntertainmentGain()
	{
		if (_externalPlayerProcess is null || _externalPlayerProcess.HasExited)
		{
			return;
		}

		var sinkInputIndex = TryResolveFfplaySinkInputIndex(_externalPlayerProcess.Id);
		if (sinkInputIndex is null)
		{
			AudioProcessorLog.Write("playback", "Could not resolve the ffplay PipeWire sink-input; entertainment gain will apply on the next change.");
			return;
		}

		// _outputGain is 0..2 (unity = 1.0). Map to a percent and cap to keep headroom below clipping.
		var percent = Math.Clamp((int)Math.Round((double)_outputGain * 100.0), 0, 150);
		var result = RunProcessCapture("pactl", $"set-sink-input-volume {sinkInputIndex.Value} {percent.ToString(CultureInfo.InvariantCulture)}%");
		if (result is null)
		{
			AudioProcessorLog.Write("playback", $"pactl set-sink-input-volume failed for sink-input {sinkInputIndex.Value}.");
			return;
		}

		AudioProcessorLog.Write("playback", $"Applied entertainment mixer gain {percent}% to ffplay sink-input {sinkInputIndex.Value}.");
	}

	/// <summary>
	/// Resolves the PipeWire sink-input index owned by the ffplay process via pactl JSON, matching on
	/// application.process.id. Returns null when pactl is unavailable or no matching input exists.
	/// </summary>
	private static int? TryResolveFfplaySinkInputIndex(int processId)
	{
		var json = RunProcessCapture("pactl", "-f json list sink-inputs");
		if (string.IsNullOrWhiteSpace(json))
		{
			AudioProcessorLog.Write("playback", "pactl returned no sink-input JSON (is PulseAudio/PipeWire-pulse available?).");
			return null;
		}

		try
		{
			using var document = JsonDocument.Parse(json);
			if (document.RootElement.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			var pidText = processId.ToString(CultureInfo.InvariantCulture);
			int? byPid = null;
			int? byBinary = null;
			var seen = new List<string>();

			foreach (var sinkInput in document.RootElement.EnumerateArray())
			{
				if (!sinkInput.TryGetProperty("index", out var indexElement) || !indexElement.TryGetInt32(out var index))
				{
					continue;
				}

				var properties = sinkInput.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
					? props
					: default;

				var appName = ReadProperty(properties, "application.name");
				var binary = ReadProperty(properties, "application.process.binary");
				var pid = ReadProperty(properties, "application.process.id");
				seen.Add($"[{index}] name='{appName}' bin='{binary}' pid='{pid}'");

				// Primary: our tagged stream. Most robust, independent of ffplay's PID reporting.
				if (string.Equals(appName, EntertainmentSinkInputAppName, StringComparison.OrdinalIgnoreCase))
				{
					return index;
				}

				if (string.Equals(pid, pidText, StringComparison.Ordinal))
				{
					byPid ??= index;
				}

				if (string.Equals(binary, "ffplay", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(appName, "ffplay", StringComparison.OrdinalIgnoreCase))
				{
					byBinary ??= index;
				}
			}

			if (byPid is not null)
			{
				return byPid;
			}

			if (byBinary is not null)
			{
				return byBinary;
			}

			AudioProcessorLog.Write("playback", $"No entertainment sink-input matched (pid {processId}). Present: {(seen.Count == 0 ? "<none>" : string.Join(" | ", seen))}");
		}
		catch (JsonException)
		{
			return null;
		}

		return null;
	}

	private static string? ReadProperty(JsonElement properties, string key)
	{
		if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(key, out var element))
		{
			return null;
		}

		return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
	}

	/// <summary>Runs a short-lived process and returns stdout, or null on failure/non-zero exit.</summary>
	private static string? RunProcessCapture(string fileName, string arguments)
	{
		try
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			LinuxRuntimeEnvironment.Apply(startInfo);
			using var process = new Process { StartInfo = startInfo };

			if (!process.Start())
			{
				return null;
			}

			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit(2000);
			return process.ExitCode == 0 ? output : null;
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return null;
		}
	}

	private void PersistActiveCommand(InternetRadioPlayCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);

		_configStore.StoredConfig.InternetRadioPlayCommandJson = System.Text.Json.JsonSerializer.Serialize(command);
		AudioProcessorLog.Write("playback", $"Persisted internet radio stream '{command.DisplayName}' for restart recovery.");
	}

	private bool CanReuseActivePlayback(InternetRadioPlayCommand command)
	{
		ArgumentNullException.ThrowIfNull(command);

		if (_activeCommand is null)
		{
			return false;
		}

		if (!string.Equals(_activeCommand.StreamUrl, command.StreamUrl, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		if (OperatingSystem.IsWindows())
		{
			return _waveOut is not null;
		}

		return OperatingSystem.IsLinux()
			&& _externalPlayerProcess is not null
			&& !_externalPlayerProcess.HasExited;
	}

	private async Task EnsureStreamReachableAsync(string streamUrl, CancellationToken cancellationToken)
	{
		if (await TryValidateStreamAsync(HttpMethod.Head, streamUrl, cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		if (await TryValidateStreamAsync(HttpMethod.Get, streamUrl, cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		throw new HttpRequestException($"Unable to open internet radio stream '{streamUrl}'.");
	}

	private async Task<bool> TryValidateStreamAsync(HttpMethod method, string streamUrl, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(method, streamUrl);

		try
		{
			using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			return response.IsSuccessStatusCode;
		}
		catch (HttpRequestException)
		{
			return false;
		}
	}

	private void StartWindowsPlayback(InternetRadioPlayCommand command)
	{
		_reader = new MediaFoundationReader(command.StreamUrl);
		_waveOut = new WaveOutEvent();
		_waveOut.Init(_reader);
		_activeBackend = "the Windows default output";
		ApplyCurrentOutputGain();
		_waveOut.Play();
	}

	private void StartLinuxPlayback(InternetRadioPlayCommand command)
	{
		var launchedPlayer = TryStartLinuxPlayer(command.StreamUrl);
		if (launchedPlayer is null)
		{
			throw new PlatformNotSupportedException(BuildLinuxPlaybackUnavailableMessage());
		}

		_externalPlayerProcess = launchedPlayer.Process;
		_externalPlayerProcess.EnableRaisingEvents = true;
		_externalPlayerProcess.Exited += OnExternalPlayerExited;
		_externalPlayerProcess.ErrorDataReceived += OnExternalPlayerDiagnosticReceived;
		_externalPlayerProcess.OutputDataReceived += OnExternalPlayerDiagnosticReceived;
		_externalPlayerProcess.BeginErrorReadLine();
		_externalPlayerProcess.BeginOutputReadLine();
		_activeBackend = launchedPlayer.BackendLabel;
		_linuxPlaybackStartedAtUtc = DateTimeOffset.UtcNow;
		_unexpectedLinuxRestartAttempts = 0;
		ClearLinuxPlayerDiagnostics();
		AudioProcessorLog.Write("playback", $"Started Linux internet radio playback via {_activeBackend} (pid {_externalPlayerProcess.Id}).");
	}

	private bool TryRestartLinuxPlaybackAfterUnexpectedExit(int exitCode)
	{
		if (!OperatingSystem.IsLinux() || _activeCommand is null)
		{
			return false;
		}

		if (_unexpectedLinuxRestartAttempts >= MaxUnexpectedLinuxRestartAttempts)
		{
			AudioProcessorLog.Write("playback", $"Linux internet radio auto-retry limit reached after ffplay exit code {exitCode}.");
			return false;
		}

		_unexpectedLinuxRestartAttempts++;
		AudioProcessorLog.Write("playback", $"Retrying Linux internet radio playback immediately after ffplay exit code {exitCode} (attempt {_unexpectedLinuxRestartAttempts} of {MaxUnexpectedLinuxRestartAttempts}).");

		var launchedPlayer = TryStartLinuxPlayer(_activeCommand.StreamUrl);
		if (launchedPlayer is null)
		{
			AudioProcessorLog.Write("playback", $"Linux internet radio retry attempt {_unexpectedLinuxRestartAttempts} failed to start a new player process.");
			return false;
		}

		_externalPlayerProcess = launchedPlayer.Process;
		_externalPlayerProcess.EnableRaisingEvents = true;
		_externalPlayerProcess.Exited += OnExternalPlayerExited;
		_externalPlayerProcess.ErrorDataReceived += OnExternalPlayerDiagnosticReceived;
		_externalPlayerProcess.OutputDataReceived += OnExternalPlayerDiagnosticReceived;
		_externalPlayerProcess.BeginErrorReadLine();
		_externalPlayerProcess.BeginOutputReadLine();
		_activeBackend = launchedPlayer.BackendLabel;
		_linuxPlaybackStartedAtUtc = DateTimeOffset.UtcNow;
		ClearLinuxPlayerDiagnostics();
		CurrentState = CurrentState with
		{
			IsPlaying = true,
			StreamUrl = _activeCommand.StreamUrl,
			DisplayName = _activeCommand.DisplayName,
			Genre = _activeCommand.Genre,
			Language = _activeCommand.Language,
			Status = "PLAYING",
			Detail = $"Internet radio stream playing on {GetPlaybackBackendDescription()} after automatic retry {_unexpectedLinuxRestartAttempts} of {MaxUnexpectedLinuxRestartAttempts}."
		};
		AudioProcessorLog.Write("playback", $"Linux internet radio playback retry succeeded via {_activeBackend} (pid {_externalPlayerProcess.Id}).");
		return true;
	}

	private LinuxPlayerLaunch? TryStartLinuxPlayer(string streamUrl)
	{
		var sinkName = _outputSpeakerDeviceId;
		var useSystemDefaultSink = string.IsNullOrWhiteSpace(sinkName)
			|| string.Equals(sinkName, AudioFrameworkCatalog.DefaultSpeakerDeviceId, StringComparison.OrdinalIgnoreCase);

		if (string.IsNullOrWhiteSpace(sinkName) && !useSystemDefaultSink)
		{
			AudioProcessorLog.Write("playback", "No PipeWire sink is selected for the AP master output.");
			return null;
		}

		var candidate = useSystemDefaultSink
			? LinuxPlayerCandidate.CreateFfplay(GetLinuxPlayerVolumePercent(), streamUrl, sinkName: null)
			: sinkName.StartsWith("alsa:", StringComparison.OrdinalIgnoreCase)
				? LinuxPlayerCandidate.CreateFfplayForAlsa(GetLinuxPlayerVolumePercent(), streamUrl, sinkName[5..])
				: LinuxPlayerCandidate.CreateFfplay(GetLinuxPlayerVolumePercent(), streamUrl, sinkName);
		AudioProcessorLog.Write("playback", $"Launching Linux internet radio player: {candidate.StartInfo.FileName} {string.Join(' ', candidate.StartInfo.ArgumentList)}");
		var process = new Process
		{
			StartInfo = candidate.StartInfo
		};

		try
		{
			if (!process.Start())
			{
				process.Dispose();
				return null;
			}

			if (process.WaitForExit(250))
			{
				process.Dispose();
				return null;
			}

			return new LinuxPlayerLaunch(process, candidate.BackendLabel);
		}
		catch (InvalidOperationException)
		{
			process.Dispose();
			return null;
		}
		catch (System.ComponentModel.Win32Exception)
		{
			process.Dispose();
			return null;
		}
	}

	private void ReleasePlaybackResources()
	{
		_waveOut?.Stop();
		_reader?.Dispose();
		_waveOut?.Dispose();
		_reader = null;
		_waveOut = null;

		if (_externalPlayerProcess is not null)
		{
			try
			{
				_isStoppingExternalPlayer = true;
				_externalPlayerProcess.Exited -= OnExternalPlayerExited;
				_externalPlayerProcess.ErrorDataReceived -= OnExternalPlayerDiagnosticReceived;
				_externalPlayerProcess.OutputDataReceived -= OnExternalPlayerDiagnosticReceived;

				if (!_externalPlayerProcess.HasExited)
				{
					_externalPlayerProcess.Kill(entireProcessTree: true);
					_externalPlayerProcess.WaitForExit(2000);
				}
			}
			catch (InvalidOperationException)
			{
			}
			finally
			{
				_externalPlayerProcess.Dispose();
				_externalPlayerProcess = null;
				_isStoppingExternalPlayer = false;
			}
		}

		_activeBackend = null;
		_linuxPlaybackStartedAtUtc = null;
	}

	private void ResetUnexpectedLinuxRestartAttemptsIfPlaybackWasStable()
	{
		if (_unexpectedLinuxRestartAttempts == 0 || _linuxPlaybackStartedAtUtc is null)
		{
			return;
		}

		var uptime = DateTimeOffset.UtcNow - _linuxPlaybackStartedAtUtc.Value;
		if (uptime < UnexpectedLinuxRestartResetWindow)
		{
			return;
		}

		AudioProcessorLog.Write("playback", $"Linux playback remained stable for {uptime.TotalSeconds:F0} seconds. Resetting the unexpected-exit retry counter.");
		_unexpectedLinuxRestartAttempts = 0;
	}

	private void ClearLinuxPlayerDiagnostics()
	{
		while (_linuxPlayerDiagnostics.TryDequeue(out _))
		{
		}
	}

	private void OnExternalPlayerDiagnosticReceived(object sender, DataReceivedEventArgs args)
	{
		if (string.IsNullOrWhiteSpace(args.Data))
		{
			return;
		}

		_linuxPlayerDiagnostics.Enqueue(args.Data);
		while (_linuxPlayerDiagnostics.Count > MaxProcessDiagnosticLines && _linuxPlayerDiagnostics.TryDequeue(out _))
		{
		}
	}

	private string GetLinuxPlayerDiagnosticsSummary()
	{
		return _linuxPlayerDiagnostics.IsEmpty
			? "No ffplay diagnostics were captured before exit."
			: string.Join(" | ", _linuxPlayerDiagnostics.ToArray());
	}

	private string GetPlaybackBackendDescription()
	{
		var backendLabel = _activeBackend ?? "the configured PipeWire output";
		var outputLabel = string.Equals(_outputSpeakerDeviceId, AudioFrameworkCatalog.DefaultSpeakerDeviceId, StringComparison.OrdinalIgnoreCase)
			? AudioFrameworkCatalog.SystemDefaultSpeakerDisplayName
			: _outputSpeakerDeviceId;
		return $"{backendLabel} routed to {outputLabel}";
	}

	private string BuildLinuxPlaybackUnavailableMessage()
	{
		var sinkName = _outputSpeakerDeviceId;
		if (string.IsNullOrWhiteSpace(sinkName)
			|| string.Equals(sinkName, AudioFrameworkCatalog.DefaultSpeakerDeviceId, StringComparison.OrdinalIgnoreCase))
		{
			return "Linux internet radio playback requires ffplay and a reachable system audio output for the AP master output.";
		}

		return $"Linux internet radio playback requires ffplay and access to the configured output '{sinkName}'.";
	}

	// ffplay always launches at a fixed level; the operator's volume is applied separately through
	// the PipeWire mixer (the stream's sink-input volume into the master output), never by changing
	// ffplay itself (per the entertainment-resource volume rule).
	private const int LinuxPlayerFixedVolumePercent = 95;

	private int GetLinuxPlayerVolumePercent()
	{
		return LinuxPlayerFixedVolumePercent;
	}

	private void OnExternalPlayerExited(object? sender, EventArgs e)
	{
		var process = sender as Process;
		if (process is null)
		{
			return;
		}

		if (_isStoppingExternalPlayer)
		{
			return;
		}

		var exitCode = process.ExitCode;
		ResetUnexpectedLinuxRestartAttemptsIfPlaybackWasStable();
		var diagnostics = GetLinuxPlayerDiagnosticsSummary();
		AudioProcessorLog.Write("playback", $"Linux internet radio player exited unexpectedly with code {exitCode}. Diagnostics: {diagnostics}");

		if (_externalPlayerProcess == process)
		{
			_externalPlayerProcess.Exited -= OnExternalPlayerExited;
			_externalPlayerProcess.ErrorDataReceived -= OnExternalPlayerDiagnosticReceived;
			_externalPlayerProcess.OutputDataReceived -= OnExternalPlayerDiagnosticReceived;
			_externalPlayerProcess.Dispose();
			_externalPlayerProcess = null;
			_activeBackend = null;

			if (TryRestartLinuxPlaybackAfterUnexpectedExit(exitCode))
			{
				return;
			}

			CurrentState = CurrentState with
			{
				IsPlaying = false,
				Status = "ERROR",
				Detail = $"Linux internet radio playback stopped unexpectedly on {GetPlaybackBackendDescription()} (ffplay exit code {exitCode})."
			};
		}
	}

	public ValueTask DisposeAsync()
	{
		Stop();
		_httpClient.Dispose();
		_playbackGate.Dispose();
		return ValueTask.CompletedTask;
	}
}

internal sealed record LinuxPlayerLaunch(Process Process, string BackendLabel);

internal sealed class LinuxPlayerCandidate
{
	private LinuxPlayerCandidate(string backendLabel, ProcessStartInfo startInfo)
	{
		BackendLabel = backendLabel;
		StartInfo = startInfo;
	}

	public string BackendLabel { get; }

	public ProcessStartInfo StartInfo { get; }

	public static LinuxPlayerCandidate CreateFfplay(int volumePercent, string streamUrl, string? sinkName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(streamUrl);

		var startInfo = CreateStartInfo("ffplay");
		if (!string.IsNullOrWhiteSpace(sinkName))
		{
			startInfo.Environment["PULSE_SINK"] = sinkName;
		}

		// Route ffplay through the Pulse/PipeWire driver and tag it with a known application.name so
		// it appears as a controllable sink-input (the entertainment mixer input to the master output).
		startInfo.Environment["SDL_AUDIODRIVER"] = "pulseaudio";
		startInfo.Environment["PULSE_PROP"] = $"application.name={InternetRadioPlaybackController.EntertainmentSinkInputAppName}";

		startInfo.ArgumentList.Add("-nodisp");
		startInfo.ArgumentList.Add("-vn");
		startInfo.ArgumentList.Add("-hide_banner");
		startInfo.ArgumentList.Add("-loglevel");
		startInfo.ArgumentList.Add("error");
		startInfo.ArgumentList.Add("-fflags");
		startInfo.ArgumentList.Add("+discardcorrupt+nobuffer");
		startInfo.ArgumentList.Add("-flags");
		startInfo.ArgumentList.Add("low_delay");
		startInfo.ArgumentList.Add("-reconnect");
		startInfo.ArgumentList.Add("1");
		startInfo.ArgumentList.Add("-reconnect_streamed");
		startInfo.ArgumentList.Add("1");
		startInfo.ArgumentList.Add("-reconnect_delay_max");
		startInfo.ArgumentList.Add("2");
		startInfo.ArgumentList.Add("-rw_timeout");
		startInfo.ArgumentList.Add("15000000");
		startInfo.ArgumentList.Add("-volume");
		startInfo.ArgumentList.Add(volumePercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
		startInfo.ArgumentList.Add(streamUrl);
		return new LinuxPlayerCandidate(
			string.IsNullOrWhiteSpace(sinkName)
				? "ffplay on the PipeWire system default output"
				: $"ffplay on PipeWire sink '{sinkName}'",
			startInfo);
	}

	public static LinuxPlayerCandidate CreateFfplayForAlsa(int volumePercent, string streamUrl, string alsaDeviceName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(streamUrl);
		ArgumentException.ThrowIfNullOrWhiteSpace(alsaDeviceName);

		var startInfo = CreateStartInfo("ffplay");
		startInfo.Environment["SDL_AUDIODRIVER"] = "alsa";
		startInfo.Environment["AUDIODEV"] = alsaDeviceName;
		startInfo.ArgumentList.Add("-nodisp");
		startInfo.ArgumentList.Add("-vn");
		startInfo.ArgumentList.Add("-hide_banner");
		startInfo.ArgumentList.Add("-loglevel");
		startInfo.ArgumentList.Add("error");
		startInfo.ArgumentList.Add("-fflags");
		startInfo.ArgumentList.Add("+discardcorrupt+nobuffer");
		startInfo.ArgumentList.Add("-flags");
		startInfo.ArgumentList.Add("low_delay");
		startInfo.ArgumentList.Add("-reconnect");
		startInfo.ArgumentList.Add("1");
		startInfo.ArgumentList.Add("-reconnect_streamed");
		startInfo.ArgumentList.Add("1");
		startInfo.ArgumentList.Add("-reconnect_delay_max");
		startInfo.ArgumentList.Add("2");
		startInfo.ArgumentList.Add("-rw_timeout");
		startInfo.ArgumentList.Add("15000000");
		startInfo.ArgumentList.Add("-volume");
		startInfo.ArgumentList.Add(volumePercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
		startInfo.ArgumentList.Add(streamUrl);
		return new LinuxPlayerCandidate($"ffplay on ALSA device '{alsaDeviceName}'", startInfo);
	}

	private static ProcessStartInfo CreateStartInfo(string fileName)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = fileName,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true
		};
		LinuxRuntimeEnvironment.Apply(startInfo);
		return startInfo;
	}
}

/// <summary>
/// Ensures child processes (ffplay, pactl) can reach the user's PulseAudio/PipeWire server when the
/// AP runs as a systemd service that lacks XDG_RUNTIME_DIR. Without this, pactl cannot find the
/// server socket (/run/user/&lt;uid&gt;/pulse/native) and device discovery falls back to raw ALSA.
/// </summary>
internal static class LinuxRuntimeEnvironment
{
	public static void Apply(ProcessStartInfo startInfo)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		if (!OperatingSystem.IsLinux())
		{
			return;
		}

		// ProcessStartInfo.Environment is seeded from the AP's own environment; only fill the gap.
		if (startInfo.Environment.TryGetValue("XDG_RUNTIME_DIR", out var existing) && !string.IsNullOrWhiteSpace(existing))
		{
			return;
		}

		try
		{
			startInfo.Environment["XDG_RUNTIME_DIR"] = $"/run/user/{geteuid()}";
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			// libc unavailable (non-Linux test host); leave the environment untouched.
		}
	}

	[DllImport("libc", SetLastError = true)]
	private static extern uint geteuid();
}