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
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyForce.Contracts.Radio;

/// <summary>
/// Defines the shared Audio Processor to Radio Module contract version.
/// </summary>
public static class RadioContract
{
	// v2: added module function buttons + console-scoped menus (§3.10, framework v2.8).
	// v3: added IModuleHost.ReportChannels so RMs can pull/report their channel list (§3.11/§5.3).
	// v4: added live per-button state to RadioStateReport.Buttons (§3.10.1, e.g. power=high -> active).
	public const int Version = 4;
}

/// <summary>
/// Describes a radio module factory that the Audio Processor can discover and instantiate.
/// </summary>
public interface IRadioModuleFactory
{
	string TypeId { get; }

	string DisplayName { get; }

	string Version { get; }

	int ContractVersion { get; }

	string ConfigSchema { get; }

	RadioCapabilities Capabilities { get; }

	IRadioModule Create(IModuleHost host);
}

/// <summary>
/// Describes the runtime behavior for a single radio module instance.
/// </summary>
public interface IRadioModule : IAsyncDisposable
{
	/// <summary>
	/// Validate the RM-owned "settings" section against ConfigSchema and apply it (§3.7.7).
	/// Only the settings subset is passed; keying/detect/device are AP-owned (§3.7.8). Idempotent;
	/// disruptive changes may defer until idle (§3.6.9).
	/// </summary>
	Task<OperationResult> ApplyConfigAsync(JsonElement settings, CancellationToken cancellationToken);

	/// <summary>Return the current applied settings.</summary>
	JsonNode GetConfig();

	Task StartAsync(CancellationToken cancellationToken);

	Task StopAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Invoke an idiosyncratic control declared in Capabilities (e.g. "channel_select"); args follow
	/// the control's ArgsSchema (§3.7.7).
	/// </summary>
	Task<OperationResult> ExecuteControlAsync(string action, JsonElement args, CancellationToken cancellationToken);

	/// <summary>
	/// A declared function button was pressed (§3.10.2, v2.8). consoleId identifies the originating
	/// console (for ShowMenuAsync). A one-shot button acts; a menu button starts a session and acks
	/// promptly. Default: not supported, so modules without buttons need no change.
	/// </summary>
	Task<OperationResult> PressButtonAsync(string buttonId, string consoleId, CancellationToken cancellationToken)
		=> Task.FromResult(OperationResult.Error($"Function button '{buttonId}' is not supported."));
}

/// <summary>
/// Provides host services that a radio module can use for AP-mediated interactions.
/// </summary>
public interface IModuleHost
{
	IControlTransport? ControlTransport { get; }

	/// <summary>Current engine RX level (0..1) for this radio; only for an RM with custom detection (§3.7.3).</summary>
	float GetRxLevel();

	/// <summary>Push state the RM knows; the AP merges rx/tx and publishes §5.8.5 (§3.7.3).</summary>
	void ReportState(RadioStateReport state);

	/// <summary>
	/// Report the radio's effective channel list (§3.11): the RM pulls it from the radio (or codeplug)
	/// and the AP publishes it retained on module/&lt;id&gt;/channels (§5.3) for the UI's channel picker.
	/// Call on StartAsync and again whenever the list changes (zone change / reprogram). Default no-op so
	/// a host built against an older contract is non-fatal.
	/// </summary>
	void ReportChannels(IReadOnlyList<ChannelInfo> channels)
	{
	}

	/// <summary>Push Call Detect when the radio's detect method is "rm"; ignored otherwise (§3.6.8).</summary>
	void ReportDetect(bool rxActive);

	/// <summary>Transient advisory event, surfaced on MQTT evt/* (§5.2).</summary>
	void EmitEvent(string name, JsonNode? data = null);

	void Log(LogLevel level, string message);

	/// <summary>
	/// Present a console-scoped menu and await the operator's input (§3.10.3, v2.8). The AP renders
	/// the JSON-Schema menu on the given console and returns the result. Default: returns a cancelled
	/// result, so a host that does not yet support menus is non-fatal.
	/// </summary>
	Task<MenuResult> ShowMenuAsync(string consoleId, MenuSpec menu, CancellationToken cancellationToken)
		=> Task.FromResult(new MenuResult(false, null));
}

/// <summary>
/// Provides the optional shared serial transport used when the AP owns a combined keying and CAT port.
/// </summary>
public interface IControlTransport
{
	Task WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

	Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>
/// Provides in-process RM-owned keying for radios that do not use AP relay keying.
/// </summary>
public interface IKeyingProvider
{
	/// <summary>Assert PTT in-process; the AP TX Controller waits ptt_lead_ms then opens the gate (§3.4, §3.6.3).</summary>
	Task KeyAsync(CancellationToken cancellationToken);

	/// <summary>De-assert PTT after the tail (§3.4).</summary>
	Task UnkeyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Exposes ARM-owned audio exchange hooks for radios that provide their own audio.
/// </summary>
public interface IAudioProvider
{
	/// <summary>The AP hands the module the in-process audio exchange around StartAsync (§3.6.2.2).</summary>
	void BindAudio(IAudioExchange exchange);
}

/// <summary>
/// In-process PCM exchange between an ARM (its own thread) and the engine, via lock-free ring
/// buffers at the engine format. Not called on the RT thread (§3.6.2.2).
/// </summary>
public interface IAudioExchange
{
	AudioFormat Format { get; }

	/// <summary>Write received-from-radio PCM into the matrix (RX).</summary>
	void WriteRx(ReadOnlySpan<float> frame);

	/// <summary>Read operator/matrix PCM destined for the radio (TX); returns samples available, 0 when not keyed.</summary>
	int ReadTx(Span<float> frame);
}

/// <summary>Engine audio format an ARM exchanges PCM at (§3.6.2.2).</summary>
public readonly record struct AudioFormat(int SampleRateHz, int FrameSamples, int Channels);

/// <summary>
/// Captures the static capabilities a radio module advertises.
/// </summary>
public sealed record RadioCapabilities(
	IReadOnlyList<KeyingMethod> Keying,
	IReadOnlyList<DetectMethod> Detect,
	bool ProvidesAudio,
	IReadOnlyList<string> Controls,
	// Up to 24 module-declared function buttons the UI renders as a panel (§3.10, v2.8).
	IReadOnlyList<FunctionButton>? Buttons = null);

/// <summary>
/// A module-declared function button (§3.10.1). OpensMenu = true means the press starts a menu
/// session (the module calls IModuleHost.ShowMenuAsync); false = a one-shot action.
/// </summary>
public sealed record FunctionButton(
	string Id,
	string Label,
	bool OpensMenu = false,
	string? Icon = null,
	int? Group = null,
	int? Order = null);

/// <summary>
/// A menu the module asks a console to display (§3.10.3). Schema is JSON Schema (2020-12) describing
/// the input fields; the UI renders it the same way it renders admin pages (§3.9.5).
/// </summary>
public sealed record MenuSpec(string Title, string Schema, JsonNode? Initial = null);

/// <summary>The operator's response to a menu. Submitted = false means cancelled.</summary>
public sealed record MenuResult(bool Submitted, JsonNode? Values);

/// <summary>
/// Describes the operator-selected keying settings for a radio instance.
/// </summary>
public sealed record KeyingConfig(
	KeyingMethod Method,
	RelayBinding? Relay,
	int PttLeadMs,
	int PttTailMs,
	bool TalkPermit);

/// <summary>
/// Describes the operator-selected detection settings for a radio instance.
/// </summary>
public sealed record DetectConfig(
	DetectMethod Method,
	VoxConfig? Vox);

/// <summary>
/// Describes the AP soundcard binding for a radio that does not provide audio through an ARM.
/// v3.0 (§3.7.8) splits the binding into separate capture (rx) and playback (tx) devices, which may
/// name the same card or two different cards. Soundcard is retained for back-compat / single-card use.
/// </summary>
public sealed record DeviceBindingConfig(string? Soundcard, string? RxDevice = null, string? TxDevice = null);

/// <summary>
/// Combines the AP-owned common radio config sections with RM-owned settings.
/// </summary>
public sealed record RadioModuleInstanceConfig(
	KeyingConfig Keying,
	DetectConfig Detect,
	DeviceBindingConfig? Device,
	JsonObject Settings);

/// <summary>
/// Describes a relay channel binding owned by the Audio Processor.
/// </summary>
public sealed record RelayBinding(string RelaySet, int Channel);

/// <summary>
/// Describes the AP-owned VOX timing and threshold settings.
/// </summary>
public sealed record VoxConfig(double ThresholdDb, int AttackMs, int HangMs);

/// <summary>
/// Describes a module-reported runtime state snapshot.
/// </summary>
public sealed record RadioStateReport(
	ChannelInfo? Channel,
	ZoneInfo? Zone,
	string? Mode,
	SignalInfo? Signal,
	bool? Ready,
	// Whether the radio is currently scanning, when the radio reports it (null = unknown).
	bool? Scan = null,
	// Live per-function-button state keyed by button id (§3.10.1): e.g. {"power": {Active=true}} renders
	// the power button active/red in the UI. Null = no button-state update in this report.
	IReadOnlyDictionary<string, RadioButtonStateReport>? Buttons = null);

/// <summary>Live state for one declared function button (§3.10.1): active/toggled, enabled, dynamic label.</summary>
public sealed record RadioButtonStateReport(bool? Active = null, bool? Enabled = null, string? Label = null);

/// <summary>
/// Describes a channel identity reported by a radio module.
/// </summary>
public sealed record ChannelInfo(int Index, string? Label);

/// <summary>
/// Describes a zone identity reported by a radio module.
/// </summary>
public sealed record ZoneInfo(int Index, string? Label);

/// <summary>
/// Describes signal metadata reported by a radio module.
/// </summary>
public sealed record SignalInfo(int? RssiDbm);

/// <summary>
/// Describes the result of a config or control operation.
/// </summary>
public sealed record OperationResult(OperationStatus Status, IReadOnlyList<FieldError>? Errors = null)
{
	public static OperationResult Ok() => new(OperationStatus.Ok);

	public static OperationResult Rejected(IReadOnlyList<FieldError> errors) => new(OperationStatus.Rejected, errors);

	public static OperationResult Error(string message) => new(OperationStatus.Error, [new FieldError(null, "error", message)]);
}

/// <summary>
/// Describes a validation or execution issue associated with a field.
/// </summary>
public sealed record FieldError(string? Field, string Code, string Message);

public enum OperationStatus
{
	Ok,

	Rejected,

	Error
}

public enum KeyingMethod
{
	Relay,

	Rm
}

public enum DetectMethod
{
	Vox,

	Rm
}

public enum LogLevel
{
	Trace,

	Debug,

	Info,

	Warning,

	Error
}

/// <summary>
/// Builds the AP-owned common radio config schema from capabilities and RM settings schema.
/// </summary>
public static class RadioModuleSchemaBuilder
{
	public static JsonObject BuildInstanceSchema(RadioCapabilities capabilities, string settingsSchemaJson)
	{
		ArgumentNullException.ThrowIfNull(capabilities);
		ArgumentException.ThrowIfNullOrWhiteSpace(settingsSchemaJson);

		var settingsSchemaNode = JsonNode.Parse(settingsSchemaJson) as JsonObject
			?? throw new JsonException("The settings schema must be a JSON object.");

		var schema = new JsonObject
		{
			["$schema"] = "https://json-schema.org/draft/2020-12/schema",
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["keying"] = BuildKeyingSchema(capabilities),
				["detect"] = BuildDetectSchema(capabilities),
				["settings"] = settingsSchemaNode.DeepClone()
			},
			["required"] = new JsonArray("keying", "detect", "settings")
		};

		if (!capabilities.ProvidesAudio)
		{
			((JsonObject)schema["properties"]!).Add("device", BuildDeviceSchema());
			((JsonArray)schema["required"]!).Add("device");
		}

		return schema;
	}

	private static JsonObject BuildKeyingSchema(RadioCapabilities capabilities)
	{
		return new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["method"] = BuildEnumSchema(capabilities.Keying.Select(static method => method.ToString().ToLowerInvariant())),
				["relay"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["relay_set"] = new JsonObject { ["type"] = "string" },
						["channel"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 }
					},
					["required"] = new JsonArray("relay_set", "channel")
				},
				["ptt_lead_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
				["ptt_tail_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
				["talk_permit"] = new JsonObject { ["type"] = "boolean" }
			},
			["required"] = new JsonArray("method", "ptt_lead_ms", "ptt_tail_ms", "talk_permit")
		};
	}

	private static JsonObject BuildDetectSchema(RadioCapabilities capabilities)
	{
		return new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["method"] = BuildEnumSchema(capabilities.Detect.Select(static method => method.ToString().ToLowerInvariant())),
				["vox"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["threshold_db"] = new JsonObject { ["type"] = "number" },
						["attack_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
						["hang_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 }
					},
					["required"] = new JsonArray("threshold_db", "attack_ms", "hang_ms")
				}
			},
			["required"] = new JsonArray("method")
		};
	}

	private static JsonObject BuildDeviceSchema()
	{
		// v3.0 (§3.7.8): separate capture (rx) and playback (tx) bindings, each a dynamic pick-list
		// (x-options) resolved against the AP's published audio-device list (§3.9.5).
		return new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["rx_device"] = new JsonObject { ["type"] = "string", ["title"] = "RX soundcard (capture)", ["x-options"] = "audio_devices.capture" },
				["tx_device"] = new JsonObject { ["type"] = "string", ["title"] = "TX soundcard (playback)", ["x-options"] = "audio_devices.playback" }
			},
			["required"] = new JsonArray("rx_device", "tx_device")
		};
	}

	private static JsonObject BuildEnumSchema(IEnumerable<string> values)
	{
		ArgumentNullException.ThrowIfNull(values);
		var enumValues = new JsonArray(values.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());

		return new JsonObject
		{
			["type"] = "string",
			["enum"] = enumValues
		};
	}
}