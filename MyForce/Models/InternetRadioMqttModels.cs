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
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

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

	public const string SpecPlayCommandTopic = "myforce/module/media.internet-radio/cmd/play";

	public const string SpecStopCommandTopic = "myforce/module/media.internet-radio/cmd/stop";

	public const string SpecGainCommandTopic = "myforce/module/media.internet-radio/cmd/gain";

	public const string SpecSpeakerOutputCommandTopic = "myforce/module/audio.processor/cmd/output-speaker";

	public const string SpecMasterVolumeCommandTopic = "myforce/module/audio.processor/cmd/master-volume";

	public const string AudioProcessorRegistryTopic = "myforce/ap/registry/service";

	public const string StateTopic = "myforce/ap/state/internet-radio";

	public const string AudioFrameworkStateTopic = "myforce/ap/state/audio-framework";

	public const string RadioRuntimeStateTopic = "myforce/ap/state/radios";

	public const string RoutingStateTopic = "myforce/ap/state/routing";

	public const string SpeakerOutputCommandTopic = "myforce/ap/cmd/output-speaker";
	public const string SystemPluginsTopic = "myforce/sys/plugins";
	public const string SystemDefinitionTopic = "myforce/sys/definition";
	public const string ModuleTopicFilter = "myforce/module/+/+";
	public const string ConsoleTxTopic = "myforce/console/tx";

	// Physical hand grip controller (HCD): it publishes the selected mode and soft-key presses; the UI
	// subscribes to drive the Hand Grip Mode display and trigger the on-screen soft keys.
	public const string HcdModeTopicFilter = "myforce/console/+/hcd/mode";
	public const string HcdSoftKeyTopicFilter = "myforce/console/+/hcd/softkey";

	// The UI republishes a soft-key activation (from touch or the HCD) so downstream mappings can react.
	public const string SoftKeyCommandTopic = "myforce/console/vip/cmd/softkey";

	// Siren Interface Controller command topics (§5.2 per-module cmd/<action>). The
	// controller's instance id is "siren1"; the firmware subscribes to cmd/# and
	// dispatches by the trailing action. "directional" drives the L/Center/R arrow
	// relays (center energises both relays), "code" drives the interlocked Code1/2/3
	// group (§ siren wiring spec).
	public const string SirenDirectionalCommandTopic = "myforce/module/siren1/cmd/directional";

	public const string SirenCodeCommandTopic = "myforce/module/siren1/cmd/code";

	// Generic per-relay on/off for the Siren Interface Controller (scene lights, air
	// horn, etc.): cmd/set with { function, state }.
	public const string SirenSetCommandTopic = "myforce/module/siren1/cmd/set";

	// This console's selected radio target (§5.4): the radio the RADIO page is viewing
	// and that the VIP PTT keys. Console id is "vip" to match the soft-key topic.
	public const string ConsoleSelectCommandTopic = "myforce/console/vip/cmd/select";

	// GPIO Relay Controller command topic (§5.2 per-module cmd/<action>). The
	// controller's instance id is "gpio.relay1"; "pulse" momentarily energises a
	// named relay then auto-releases it, used by the camera REC/STOP/AUTOZ buttons
	// to simulate a button press on the camera/DVR.
	public const string GpioPulseCommandTopic = "myforce/module/gpio.relay1/cmd/pulse";
}

/// <summary>HCD-published hand grip mode (lights / radio / patrol).</summary>
internal sealed record HcdModeMessage(string? Mode);

/// <summary>HCD-published soft-key press (1-6).</summary>
internal sealed record HcdSoftKeyMessage(int Index);

/// <summary>UI-published soft-key activation, with the active hand grip mode for context.</summary>
internal sealed record SoftKeyCommandMessage(
	int V,
	DateTimeOffset Ts,
	string? MsgId,
	string? Auth,
	int Index,
	string Mode);

/// <summary>
/// UI-published directional command for the Siren Interface Controller
/// (myforce/module/siren1/cmd/directional, §5.2). Direction is one of
/// off | left | center | right; "center" energises both directional relays at once.
/// MsgId carries the explicit snake_case name (msg_id) the ESP32 firmware reads (§5.8.1).
/// </summary>
internal sealed record SirenDirectionalCommandMessage(
	int V,
	DateTimeOffset Ts,
	[property: JsonPropertyName("msg_id")] string? MsgId,
	string? Auth,
	[property: JsonPropertyName("direction")] string Direction);

/// <summary>
/// UI-published siren-code command for the Siren Interface Controller
/// (myforce/module/siren1/cmd/code, §5.2). Code is one of off | code1 | code2 | code3,
/// the mutually-exclusive interlocked code group. MsgId carries the snake_case name
/// (msg_id) the ESP32 firmware reads (§5.8.1).
/// </summary>
internal sealed record SirenCodeCommandMessage(
	int V,
	DateTimeOffset Ts,
	[property: JsonPropertyName("msg_id")] string? MsgId,
	string? Auth,
	[property: JsonPropertyName("code")] string Code);

/// <summary>
/// UI-published per-relay on/off command for the Siren Interface Controller
/// (myforce/module/siren1/cmd/set, §5.2). Function is a relay function name
/// (e.g. "alley_left", "takedown", "airhorn"); State is "on" or "off". Used by the
/// L/S page scene-light toggles and the air horn. MsgId carries the snake_case
/// name (msg_id) the ESP32 firmware reads (§5.8.1).
/// </summary>
internal sealed record SirenSetCommandMessage(
	int V,
	DateTimeOffset Ts,
	[property: JsonPropertyName("msg_id")] string? MsgId,
	string? Auth,
	[property: JsonPropertyName("function")] string Function,
	[property: JsonPropertyName("state")] string State);

/// <summary>
/// UI-published console radio selection (myforce/console/vip/cmd/select, §5.4): the
/// radio this console is viewing / will key. Target is the radio's module id.
/// </summary>
internal sealed record ConsoleSelectCommandMessage(
	int V,
	DateTimeOffset Ts,
	[property: JsonPropertyName("msg_id")] string? MsgId,
	string? Auth,
	[property: JsonPropertyName("target")] string Target);

/// <summary>
/// UI-published momentary pulse command for the GPIO Relay Controller
/// (myforce/module/gpio.relay1/cmd/pulse, §5.2). The firmware energises the named
/// relay (Function) for Ms milliseconds then auto-releases it, simulating a button
/// press on the camera/DVR. MsgId carries the snake_case name (msg_id) the ESP32
/// firmware reads (§5.8.1).
/// </summary>
internal sealed record GpioPulseCommandMessage(
	int V,
	DateTimeOffset Ts,
	[property: JsonPropertyName("msg_id")] string? MsgId,
	string? Auth,
	[property: JsonPropertyName("function")] string Function,
	[property: JsonPropertyName("ms")] int Ms);

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

public sealed record AudioProcessorRegistryMessage(
	string ServiceId,
	string DisplayName,
	IReadOnlyList<RadioRegistryEntryMessage> Radios,
	IReadOnlyList<string> RadioIds,
	IReadOnlyList<string> BridgeIds);

public sealed record RadioRegistryEntryMessage(
	string RadioId,
	string TypeId,
	string DisplayName,
	string Kind,
	RadioCapabilitiesMessage Capabilities,
	string ConfigSchema,
	string InstanceSchema);

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

internal sealed record ModuleRegistrySpecMessage(
	int V,
	DateTimeOffset Ts,
	string Id,
	[property: JsonPropertyName("type_id")] string TypeId,
	string Kind,
	string Category,
	bool Removable,
	[property: JsonPropertyName("config_schema")] JsonElement ConfigSchema,
	RadioCapabilitiesMessage Capabilities);

internal sealed record ModuleStatusSpecMessage(
	int V,
	DateTimeOffset Ts,
	string Id,
	bool Online,
	string Health,
	string? Reason);

internal sealed record ModuleRadioStateSpecMessage(
	int V,
	DateTimeOffset Ts,
	string Id,
	[property: JsonPropertyName("rx_active")] bool RxActive,
	[property: JsonPropertyName("tx_active")] bool TxActive,
	[property: JsonPropertyName("tx_source")] string? TxSource,
	ChannelInfoMessage? Channel,
	ZoneInfoMessage? Zone,
	string? Mode,
	SignalInfoMessage? Signal);

internal sealed record ChannelInfoMessage(int Index, string? Label);

internal sealed record ZoneInfoMessage(int Index, string? Label);

internal sealed record SignalInfoMessage([property: JsonPropertyName("rssi_dbm")] int? RssiDbm);

internal sealed record ConsoleTxStateMessage(int V, DateTimeOffset Ts, string? Holder, string? Target, string State);

internal sealed record CommandAckMessage(
	int V,
	DateTimeOffset Ts,
	[property: JsonPropertyName("msg_id")] string MsgId,
	string Status,
	IReadOnlyList<CommandAckErrorMessage>? Errors);

internal sealed record CommandAckErrorMessage(string? Field, string Code, string Message);
