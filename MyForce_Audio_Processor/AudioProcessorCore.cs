using System.Buffers;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using MQTTnet;
using NAudio.Wave;

internal sealed class AudioProcessorCoordinator : IAsyncDisposable
{
    private readonly AudioProcessorRegistry _registry;
    private readonly AudioFrameworkCatalog _audioFramework;
    private readonly InternetRadioPlaybackController _internetRadioController;
    private readonly AudioMixerState _mixerState;
    private readonly AudioProcessorRoutingState _routingState;
    private readonly MqttServiceRuntime _mqttRuntime;
    private readonly AudioProcessorTopicFactory _topics;
    private readonly TxController _txController;

    public AudioProcessorCoordinator(MqttServiceRuntime mqttRuntime, AudioProcessorTopicFactory topics)
    {
        ArgumentNullException.ThrowIfNull(mqttRuntime);
        ArgumentNullException.ThrowIfNull(topics);

        _mqttRuntime = mqttRuntime;
        _topics = topics;
        _registry = AudioProcessorRegistry.CreateDefault();
        _audioFramework = AudioFrameworkCatalog.CreateDefault(_registry.RadioIds);
        _internetRadioController = new InternetRadioPlaybackController();
        _mixerState = AudioMixerState.CreateDefault(_audioFramework.ChannelStrips);
        _routingState = AudioProcessorRoutingState.CreateDefault(_registry.RadioIds);
        _txController = new TxController(_registry.RadioIds);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _mqttRuntime.SubscribeAsync(_topics.AllCommandsTopicFilter, cancellationToken).ConfigureAwait(false);
        await PublishBirthSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reapplies retained subscriptions and republishes the current AP health snapshot after MQTT reconnects.
    /// </summary>
    public async Task HandleConnectedAsync(CancellationToken cancellationToken)
    {
        await _mqttRuntime.SubscribeAsync(_topics.AllCommandsTopicFilter, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(topic, _topics.ManualPttRequestTopic, StringComparison.OrdinalIgnoreCase))
        {
            var request = AudioProcessorJson.Deserialize<ManualPttRequest>(args.ApplicationMessage.Payload);
            if (request is null)
            {
                return;
            }

            ApplyManualPtt(request);
            await PublishMixerStateAsync(CancellationToken.None).ConfigureAwait(false);
            await PublishStatusAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (string.Equals(topic, _topics.ChannelGainCommandTopic, StringComparison.OrdinalIgnoreCase))
        {
            var command = AudioProcessorJson.Deserialize<AudioChannelGainCommand>(args.ApplicationMessage.Payload);
            if (command is null)
            {
                return;
            }

            _mixerState.SetGain(command.ChannelId, command.Gain);
            await PublishMixerStateAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (string.Equals(topic, _topics.ChannelMuteCommandTopic, StringComparison.OrdinalIgnoreCase))
        {
            var command = AudioProcessorJson.Deserialize<AudioChannelMuteCommand>(args.ApplicationMessage.Payload);
            if (command is null)
            {
                return;
            }

            _mixerState.SetMuted(command.ChannelId, command.IsMuted);
            await PublishMixerStateAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (string.Equals(topic, _topics.InternetRadioPlayCommandTopic, StringComparison.OrdinalIgnoreCase))
        {
            var command = AudioProcessorJson.Deserialize<InternetRadioPlayCommand>(args.ApplicationMessage.Payload);
            if (command is null)
            {
                return;
            }

            await _internetRadioController.PlayAsync(command, CancellationToken.None).ConfigureAwait(false);
            await PublishInternetRadioStateAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (string.Equals(topic, _topics.InternetRadioStopCommandTopic, StringComparison.OrdinalIgnoreCase))
        {
            _internetRadioController.Stop();
            await PublishInternetRadioStateAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _internetRadioController.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes a recurring AP heartbeat so the UI can actively detect stale component status.
    /// </summary>
    public async Task PublishHeartbeatAsync(CancellationToken cancellationToken)
    {
        await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ApplyManualPtt(ManualPttRequest request)
    {
        if (request.IsPressed)
        {
            _txController.BeginManualTransmit(request.RadioId);
            _routingState.SetOperatorMicTarget(request.RadioId);
            _mixerState.SetChannelActive(AudioChannelId.OperatorMic, true);
            _mixerState.SetTransmitTarget(request.RadioId, true);
            return;
        }

        _txController.EndManualTransmit(request.RadioId);
        _routingState.ClearOperatorMicTarget(request.RadioId);
        _mixerState.SetTransmitTarget(request.RadioId, false);

        if (_txController.ActiveManualTransmitRadioId is null)
        {
            _mixerState.SetChannelActive(AudioChannelId.OperatorMic, false);
        }
    }

    private async Task PublishBirthSnapshotAsync(CancellationToken cancellationToken)
    {
        await _mqttRuntime.PublishAsync(
            _topics.ServiceRegistryTopic,
            AudioProcessorJson.Serialize(ServiceRegistryPayload.Create(_registry)),
            retain: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await _mqttRuntime.PublishAsync(
            _topics.RoutingStateTopic,
            AudioProcessorJson.Serialize(RoutingStatePayload.Create(_routingState.CurrentSnapshot)),
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
        await _mqttRuntime.PublishAsync(
            _topics.AudioMixerStateTopic,
            AudioProcessorJson.Serialize(AudioMixerStatePayload.Create(_mixerState.CurrentSnapshot)),
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
                    activeManualTransmitRadioId: _txController.ActiveManualTransmitRadioId?.Value)),
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
    public AudioProcessorRegistry(IReadOnlyList<RadioId> radioIds, IReadOnlyList<BridgeDefinition> bridges)
    {
        ArgumentNullException.ThrowIfNull(radioIds);
        ArgumentNullException.ThrowIfNull(bridges);

        RadioIds = radioIds;
        Bridges = bridges;
    }

    public IReadOnlyList<RadioId> RadioIds { get; }

    public IReadOnlyList<BridgeDefinition> Bridges { get; }

    public static AudioProcessorRegistry CreateDefault()
    {
        var radios = new List<RadioId>
        {
            new("barrett"),
            new("xpr"),
            new("mtm5400"),
            new("apx-xtl"),
            new("harris"),
            new("4w")
        };

        return new AudioProcessorRegistry(radios.AsReadOnly(), Array.Empty<BridgeDefinition>());
    }
}

internal sealed class AudioProcessorRoutingState
{
    private RoutingSnapshot _currentSnapshot;

    private AudioProcessorRoutingState(RoutingSnapshot currentSnapshot)
    {
        _currentSnapshot = currentSnapshot;
    }

    public RoutingSnapshot CurrentSnapshot => _currentSnapshot;

    public static AudioProcessorRoutingState CreateDefault(IEnumerable<RadioId> radioIds)
    {
        ArgumentNullException.ThrowIfNull(radioIds);

        var crosspoints = radioIds
            .Select(static radioId => new RoutingCrosspoint(SourceEndpoint.OperatorMic, SinkEndpoint.ForRadioTx(radioId), 1.0m, false))
            .ToArray();

        return new AudioProcessorRoutingState(new RoutingSnapshot(new ReadOnlyCollection<RoutingCrosspoint>(crosspoints), SinkEndpoint.Speaker, null));
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

internal sealed class TxController
{
    private readonly HashSet<RadioId> _knownRadioIds;

    public TxController(IEnumerable<RadioId> radioIds)
    {
        ArgumentNullException.ThrowIfNull(radioIds);
        _knownRadioIds = new HashSet<RadioId>(radioIds);
    }

    public RadioId? ActiveManualTransmitRadioId { get; private set; }

    public void BeginManualTransmit(RadioId radioId)
    {
        ArgumentNullException.ThrowIfNull(radioId);

        if (!_knownRadioIds.Contains(radioId))
        {
            throw new InvalidOperationException($"Unknown radio id '{radioId.Value}'.");
        }

        ActiveManualTransmitRadioId = radioId;
    }

    public void EndManualTransmit(RadioId radioId)
    {
        ArgumentNullException.ThrowIfNull(radioId);

        if (ActiveManualTransmitRadioId == radioId)
        {
            ActiveManualTransmitRadioId = null;
        }
    }
}

internal sealed class AudioProcessorTopicFactory
{
    private const string RootTopic = "myforce/ap";

    public string AllCommandsTopicFilter => $"{RootTopic}/cmd/#";

    public string ManualPttRequestTopic => $"{RootTopic}/cmd/manual-ptt";

    public string ChannelGainCommandTopic => $"{RootTopic}/cmd/channel-gain";

    public string ChannelMuteCommandTopic => $"{RootTopic}/cmd/channel-mute";

    public string InternetRadioPlayCommandTopic => $"{RootTopic}/cmd/internet-radio/play";

    public string InternetRadioStopCommandTopic => $"{RootTopic}/cmd/internet-radio/stop";

    public string AudioFrameworkTopic => $"{RootTopic}/state/audio-framework";

    public string AudioMixerStateTopic => $"{RootTopic}/state/audio-mixer";

    public string InternetRadioStateTopic => $"{RootTopic}/state/internet-radio";

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

    public static AudioFrameworkCatalog CreateDefault(IEnumerable<RadioId> radioIds)
    {
        ArgumentNullException.ThrowIfNull(radioIds);

        var radioIdList = radioIds.ToArray();
        var devices = new List<AudioDevice>
        {
            new(new AudioDeviceId("operator-console"), "Operator Console", "operator", true, true),
            new(new AudioDeviceId("cabin-speaker"), "Cabin Speaker", "speaker", false, true),
            new(new AudioDeviceId("voice-recorder"), "Voice Recorder", "recorder", false, true)
        };

        devices.AddRange(radioIdList.Select(static radioId =>
            new AudioDevice(new AudioDeviceId($"radio-{radioId.Value}"), $"Radio {radioId.Value}", "radio", true, true)));

        var channels = new List<AudioChannelStrip>
        {
            new(AudioChannelId.OperatorMic, "Operator Mic", "operator-mic -> tx-bus", "operator", 1.0m, false, true),
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
            .Where(static channel => channel.Id == AudioChannelId.SpeakerMonitor || channel.Id.Value.EndsWith("-rx", StringComparison.Ordinal))
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

    public static SinkEndpoint ForRadioTx(RadioId radioId)
    {
        ArgumentNullException.ThrowIfNull(radioId);
        return new SinkEndpoint("radio-tx", radioId.Value);
    }
}

internal sealed record ManualPttRequest(RadioId RadioId, bool IsPressed);

internal sealed record AudioChannelGainCommand(string ChannelId, decimal Gain);

internal sealed record AudioChannelMuteCommand(string ChannelId, bool IsMuted);

internal sealed record InternetRadioPlayCommand(string StreamUrl, string DisplayName, string Genre, string Language);

internal sealed record InternetRadioPlaybackState(bool IsPlaying, string? StreamUrl, string? DisplayName, string? Genre, string? Language, string Status, string Detail);

internal sealed record ServiceRegistryPayload(
    string ServiceId,
    string DisplayName,
    IReadOnlyList<string> RadioIds,
    IReadOnlyList<string> BridgeIds)
{
    public static ServiceRegistryPayload Create(AudioProcessorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return new ServiceRegistryPayload(
            ServiceId: "audio-processor",
            DisplayName: "Audio Processor",
            RadioIds: registry.RadioIds.Select(static radioId => radioId.Value).ToArray(),
            BridgeIds: registry.Bridges.Select(static bridge => bridge.Id.Value).ToArray());
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
    IReadOnlyList<RoutingCrosspointPayload> Crosspoints)
{
    public static RoutingStatePayload Create(RoutingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new RoutingStatePayload(
            ActiveOperatorTarget: snapshot.ActiveOperatorTarget?.Value,
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
}

internal sealed class InternetRadioPlaybackController : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private IWavePlayer? _waveOut;
    private MediaFoundationReader? _reader;

    public InternetRadioPlaybackController()
    {
        _httpClient = new HttpClient();
        CurrentState = new InternetRadioPlaybackState(false, null, null, null, null, "IDLE", "No internet radio stream selected.");
    }

    public InternetRadioPlaybackState CurrentState { get; private set; }

    /// <summary>
    /// Starts internet radio playback on the default output device using the provided stream metadata.
    /// </summary>
    public async Task PlayAsync(InternetRadioPlayCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.StreamUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DisplayName);

        using var request = new HttpRequestMessage(HttpMethod.Head, command.StreamUrl);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        Stop();

        _reader = new MediaFoundationReader(command.StreamUrl);
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_reader);
        _waveOut.Play();

        CurrentState = new InternetRadioPlaybackState(
            IsPlaying: true,
            StreamUrl: command.StreamUrl,
            DisplayName: command.DisplayName,
            Genre: command.Genre,
            Language: command.Language,
            Status: "PLAYING",
            Detail: "Internet radio stream playing on the default output.");
    }

    /// <summary>
    /// Stops the current internet radio stream and releases playback resources.
    /// </summary>
    public void Stop()
    {
        _waveOut?.Stop();
        _reader?.Dispose();
        _waveOut?.Dispose();
        _reader = null;
        _waveOut = null;

        CurrentState = CurrentState with
        {
            IsPlaying = false,
            Status = "STOPPED",
            Detail = "Internet radio playback stopped."
        };
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
