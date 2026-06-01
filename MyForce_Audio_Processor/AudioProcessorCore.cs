using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using MQTTnet;
using NAudio.Wave;

internal sealed class AudioProcessorCoordinator : IAsyncDisposable
{
    private readonly AudioProcessorRegistry _registry;
    private readonly AudioFrameworkCatalog _audioFramework;
    private readonly AudioProcessorConfigStore _configStore;
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
        _configStore = new AudioProcessorConfigStore();
        _registry = AudioProcessorRegistry.CreateDefault();
        _audioFramework = AudioFrameworkCatalog.CreateDefault(_registry.RadioIds, AudioFrameworkCatalog.DiscoverPlaybackDevices());
        _internetRadioController = new InternetRadioPlaybackController(_configStore);
        _mixerState = AudioMixerState.CreateDefault(_audioFramework.ChannelStrips);
        _routingState = AudioProcessorRoutingState.CreateDefault(_registry.RadioIds, ResolveInitialSpeakerDeviceId());
        _txController = new TxController(_registry.RadioIds);
        _internetRadioController.SetOutputSpeaker(_routingState.CurrentSnapshot.SpeakerSink.DeviceId);
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

        if (string.Equals(topic, _topics.OutputSpeakerCommandTopic, StringComparison.OrdinalIgnoreCase))
        {
            var command = AudioProcessorJson.Deserialize<OutputSpeakerCommand>(args.ApplicationMessage.Payload);
            if (command is null)
            {
                return;
            }

            ApplyOutputSpeaker(command.DeviceId);
            await PublishRoutingStateAsync(CancellationToken.None).ConfigureAwait(false);
            await PublishInternetRadioStateAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (string.Equals(topic, _topics.AudioOutputConfigCommandTopic, StringComparison.OrdinalIgnoreCase))
        {
            var command = AudioProcessorJson.Deserialize<AudioOutputConfigCommand>(args.ApplicationMessage.Payload);
            if (command is null)
            {
                return;
            }

            ApplyAudioOutputConfig(command);
            await PublishRoutingStateAsync(CancellationToken.None).ConfigureAwait(false);
            await PublishInternetRadioStateAsync(CancellationToken.None).ConfigureAwait(false);
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
            _internetRadioController.SetOutputGain(command.ChannelId, command.Gain);
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
        _configStore.StoredConfig.OutputSpeakerDeviceId = deviceId;
        _internetRadioController.SetOutputSpeaker(deviceId);
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

    public string AudioOutputConfigCommandTopic => $"{RootTopic}/cmd/audio-output-config";

    public string OutputSpeakerCommandTopic => $"{RootTopic}/cmd/output-speaker";

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
                    return devicesFromJson;
                }
            }

            var sinkShortList = TryRunProcess("pactl", "list short sinks");
            if (!string.IsNullOrWhiteSpace(sinkShortList))
            {
                var devicesFromShortList = ParsePlaybackDevicesFromShortList(sinkShortList);
                if (devicesFromShortList.Count > 0)
                {
                    return devicesFromShortList;
                }
            }

            var alsaHardwareList = TryRunProcess("aplay", "-l");
            if (!string.IsNullOrWhiteSpace(alsaHardwareList))
            {
                var devicesFromAlsa = ParsePlaybackDevicesFromAlsaHardwareList(alsaHardwareList);
                if (devicesFromAlsa.Count > 0)
                {
                    return devicesFromAlsa;
                }
            }

            return Array.Empty<AudioDevice>();
        }
        catch (JsonException)
        {
            return Array.Empty<AudioDevice>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<AudioDevice>();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Array.Empty<AudioDevice>();
        }
    }

    private static string? TryRunProcess(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

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

internal sealed record ManualPttRequest(RadioId RadioId, bool IsPressed);

internal sealed record AudioChannelGainCommand(string ChannelId, decimal Gain);

internal sealed record AudioChannelMuteCommand(string ChannelId, bool IsMuted);

internal sealed record AudioOutputConfigCommand(string DeviceId, string? CabinSpeakerPipeWireSinkName, string? HeadrestSpeakerPipeWireSinkName);

internal sealed record OutputSpeakerCommand(string DeviceId);

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
    private readonly AudioProcessorConfigStore _configStore;
    private readonly HttpClient _httpClient;
    private Process? _externalPlayerProcess;
    private bool _isStoppingExternalPlayer;
    private IWavePlayer? _waveOut;
    private MediaFoundationReader? _reader;
    private InternetRadioPlayCommand? _activeCommand;
    private string? _activeBackend;
    private string _outputSpeakerDeviceId = AudioFrameworkCatalog.DefaultSpeakerDeviceId;
    private decimal _outputGain = 1.0m;

    public InternetRadioPlaybackController(AudioProcessorConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(configStore);

        _configStore = configStore;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MyForce-AudioProcessor/1.0");
        CurrentState = new InternetRadioPlaybackState(false, null, null, null, null, "IDLE", "No internet radio stream selected.");
    }

    public InternetRadioPlaybackState CurrentState { get; private set; }

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

        if (CanReuseActivePlayback(command))
        {
            _activeCommand = command;
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

        CurrentState = new InternetRadioPlaybackState(
            IsPlaying: true,
            StreamUrl: command.StreamUrl,
            DisplayName: command.DisplayName,
            Genre: command.Genre,
            Language: command.Language,
            Status: "PLAYING",
            Detail: $"Internet radio stream playing on {GetPlaybackBackendDescription()}.");
    }

    /// <summary>
    /// Stops the current internet radio stream and releases playback resources.
    /// </summary>
    public void Stop()
    {
        ReleasePlaybackResources();
        _activeCommand = null;

        CurrentState = CurrentState with
        {
            IsPlaying = false,
            Status = "STOPPED",
            Detail = "Internet radio playback stopped."
        };
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
            CurrentState = CurrentState with
            {
                Detail = $"Internet radio stream playing on {GetPlaybackBackendDescription()}. Entertainment gain is controlled by the AP mixer state."
            };
        }
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
        _activeBackend = launchedPlayer.BackendLabel;
        Console.WriteLine($"[audio-processor] Started Linux internet radio playback via {_activeBackend} (pid {_externalPlayerProcess.Id}).");
    }

    private LinuxPlayerLaunch? TryStartLinuxPlayer(string streamUrl)
    {
        var sinkName = _outputSpeakerDeviceId;
        var useSystemDefaultSink = string.IsNullOrWhiteSpace(sinkName)
            || string.Equals(sinkName, AudioFrameworkCatalog.DefaultSpeakerDeviceId, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(sinkName) && !useSystemDefaultSink)
        {
            Console.WriteLine("[audio-processor] No PipeWire sink is selected for the AP master output.");
            return null;
        }

        var candidate = useSystemDefaultSink
            ? LinuxPlayerCandidate.CreateFfplay(GetLinuxPlayerVolumePercent(), streamUrl, sinkName: null)
            : sinkName.StartsWith("alsa:", StringComparison.OrdinalIgnoreCase)
                ? LinuxPlayerCandidate.CreateFfplayForAlsa(GetLinuxPlayerVolumePercent(), streamUrl, sinkName[5..])
                : LinuxPlayerCandidate.CreateFfplay(GetLinuxPlayerVolumePercent(), streamUrl, sinkName);
        Console.WriteLine($"[audio-processor] Launching Linux internet radio player: {candidate.StartInfo.FileName} {string.Join(' ', candidate.StartInfo.ArgumentList)}");
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

    private int GetLinuxPlayerVolumePercent()
    {
        return 100;
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
        Console.WriteLine($"[audio-processor] Linux internet radio player exited unexpectedly with code {exitCode}.");

        if (_externalPlayerProcess == process)
        {
            _externalPlayerProcess.Exited -= OnExternalPlayerExited;
            _externalPlayerProcess.Dispose();
            _externalPlayerProcess = null;
            _activeBackend = null;
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

        startInfo.ArgumentList.Add("-nodisp");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
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
        startInfo.ArgumentList.Add("-volume");
        startInfo.ArgumentList.Add(volumePercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(streamUrl);
        return new LinuxPlayerCandidate($"ffplay on ALSA device '{alsaDeviceName}'", startInfo);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = false,
            RedirectStandardOutput = false
        };
    }
}
