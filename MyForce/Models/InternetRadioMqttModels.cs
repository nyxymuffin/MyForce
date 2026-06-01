using System;
using System.Collections.Generic;

namespace MyForce.Models;

/// <summary>
/// Section 5.8.1 common MQTT envelope fields applied to UI command payloads.
/// </summary>
internal sealed record MqttCommandEnvelope(
	int V,
	DateTimeOffset Ts,
	string? MsgId,
	string? Auth);

/// <summary>
/// Defines the MQTT topics used by the UI and AP for internet radio control.
/// </summary>
internal static class InternetRadioMqttTopics
{
	public const string PlayCommandTopic = "myforce/ap/cmd/internet-radio/play";
	public const string StopCommandTopic = "myforce/ap/cmd/internet-radio/stop";
	public const string StateTopic = "myforce/ap/state/internet-radio";
	public const string AudioFrameworkStateTopic = "myforce/ap/state/audio-framework";
	public const string RadioRuntimeStateTopic = "myforce/ap/state/radios";
	public const string RoutingStateTopic = "myforce/ap/state/routing";
	public const string SpeakerOutputCommandTopic = "myforce/ap/cmd/output-speaker";
}

/// <summary>
/// Represents a UI request for the AP to start internet radio playback.
/// </summary>
internal sealed record InternetRadioPlayCommandMessage(
	int V,
	DateTimeOffset Ts,
	string? MsgId,
	string? Auth,
	string StreamUrl,
	string DisplayName,
	string Genre,
	string Language);

/// <summary>
/// Represents the retained AP internet radio playback state sent over MQTT.
/// </summary>
internal sealed record InternetRadioPlaybackStateMessage(
	bool IsPlaying,
	string? StreamUrl,
	string? DisplayName,
	string? Genre,
	string? Language,
	string Status,
	string Detail);

/// <summary>
/// Represents a UI request to update an AP mixer channel gain.
/// </summary>
internal sealed record AudioChannelGainCommandMessage(
	int V,
	DateTimeOffset Ts,
	string? MsgId,
	string? Auth,
	string ChannelId,
	decimal Gain);

/// <summary>
/// Represents a UI request to update the AP master speaker output selection.
/// </summary>
internal sealed record OutputSpeakerCommandMessage(
	int V,
	DateTimeOffset Ts,
	string? MsgId,
	string? Auth,
	string DeviceId);

/// <summary>
/// Represents an AP audio device exposed in the retained audio framework payload.
/// </summary>
public sealed record AudioDeviceStateMessage(
	string DeviceId,
	string DisplayName,
	string Role,
	bool InputEnabled,
	bool OutputEnabled);

/// <summary>
/// Represents the retained AP audio framework used by the admin audio page.
/// </summary>
internal sealed record AudioFrameworkStateMessage(
	string ServiceId,
	IReadOnlyList<AudioDeviceStateMessage> Devices);

/// <summary>
/// Represents the retained AP routing state including the selected master speaker output.
/// </summary>
internal sealed record RoutingStateMessage(
	string? ActiveOperatorTarget,
	string SpeakerDeviceId);

/// <summary>
/// Represents the retained AP radio runtime payload used for schema-driven admin integration.
/// </summary>
public sealed record RadioRuntimeStateMessage(
	IReadOnlyList<RadioRuntimeEntryMessage> Radios);

/// <summary>
/// Represents one radio entry from the retained AP radio runtime payload.
/// </summary>
public sealed record RadioRuntimeEntryMessage(
	string RadioId,
	string TypeId,
	string DisplayName,
	string Kind,
	RadioCapabilitiesMessage Capabilities,
	string ConfigSchema,
	string InstanceSchema,
	RadioInstanceConfigMessage Config,
	RadioTxStateMessage TxState);

public sealed record RadioCapabilitiesMessage(
	IReadOnlyList<string> Keying,
	IReadOnlyList<string> Detect,
	bool ProvidesAudio,
	IReadOnlyList<string> Controls);

public sealed record RadioInstanceConfigMessage(
	RadioKeyingConfigMessage Keying,
	RadioDetectConfigMessage Detect,
	RadioDeviceBindingMessage? Device);

public sealed record RadioKeyingConfigMessage(
	string Method,
	RadioRelayBindingMessage? Relay,
	int PttLeadMs,
	int PttTailMs,
	bool TalkPermit);

public sealed record RadioRelayBindingMessage(
	string RelaySet,
	int Channel);

public sealed record RadioDetectConfigMessage(
	string Method,
	RadioVoxConfigMessage? Vox);

public sealed record RadioVoxConfigMessage(
	double ThresholdDb,
	int AttackMs,
	int HangMs);

public sealed record RadioDeviceBindingMessage(
	string? Soundcard);

public sealed record RadioTxStateMessage(
	string Phase,
	bool IsKeyAsserted,
	bool IsTalkPermitReady,
	string KeyingMethod,
	int PttLeadMs,
	int PttTailMs,
	DateTimeOffset LastTransitionUtc);
