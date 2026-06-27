// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// Barrett 2050 HF radio module. Drives the transceiver over RS232 (see Barrett2050Link), exposing:
//   Controls     : channel_select (XC), scan start/stop (XN), channel_info (IDL)
//   Keying (PTT) : Select Tx/rx (XP1/XP0) via IKeyingProvider, RX uses AP VOX detection
//   Function btns: Transmit Selcall (XZN), Transmit Pagecall (XZM), Clarifier (IF/EF),
//                  Power Level (IH/EH) - the get-current ones prefill their menus.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyForce.Contracts.Radio;

namespace MyForce.RadioModules.Barrett;

/// <summary>
/// Discovery entry point for the Barrett 2050 radio module. The Audio Processor loads this assembly
/// from its plugins folder and instantiates this factory by reflection.
/// </summary>
public sealed class Barrett2050ModuleFactory : IRadioModuleFactory
{
	public string TypeId => "barrett_2050";

	public string DisplayName => "Barrett 2050";

	public string Version => "0.6.0";

	public int ContractVersion => RadioContract.Version;

	// RM-owned settings sub-schema (the AP wraps this with the common keying/detect/device sections).
	// com_port + baud are the CAT/keying serial line; used only when the AP does not own a shared port.
	public string ConfigSchema => """
		{
		  "$schema": "https://json-schema.org/draft/2020-12/schema",
		  "type": "object",
		  "properties": {
		    "com_port": { "type": "string", "title": "CAT serial port", "description": "e.g. /dev/ttyUSB0 or COM3 (blank = use the AP shared control port)" },
		    "baud":     { "type": "integer", "title": "Baud rate", "default": 9600, "minimum": 1200 },
		    "selcall_id": { "type": "string", "title": "This station's Selcall ID", "pattern": "^[0-9]{4}([0-9]{2})?$" }
		  },
		  "additionalProperties": false
		}
		""";

	// Keying: AP relay OR RM RS232 PTT (Select Tx/rx). Detect: AP VOX (RX, "inbound"). No own audio.
	// Function buttons (§3.10, v2.8): the four Barrett-specific operating actions.
	public RadioCapabilities Capabilities { get; } = new(
		Keying: [KeyingMethod.Relay, KeyingMethod.Rm],
		Detect: [DetectMethod.Vox],
		ProvidesAudio: false,
		Controls: ["channel_select", "scan", "channel_info"],
		Buttons:
		[
			new FunctionButton("selcall", "Selcall", OpensMenu: true, Order: 1),
			new FunctionButton("pagecall", "Pagecall", OpensMenu: true, Order: 2),
			new FunctionButton("clarifier", "Clarifier", OpensMenu: true, Order: 3),
			new FunctionButton("power", "Power", OpensMenu: false, Order: 4),
		]);

	public IRadioModule Create(IModuleHost host) => new Barrett2050Module(host);
}

/// <summary>Runtime behaviour for a single Barrett 2050 instance, keyed via RS232 Select Tx/rx.</summary>
public sealed class Barrett2050Module : IRadioModule, IKeyingProvider
{
	// Poll the radio every 25 s with "IV" to detect whether it is online (any reply = present).
	// Status (online/scan/power) polls every 10s; the current channel (IC) polls at boot and every 60s
	// (StatusPollInterval x ChannelPollEveryTicks) to keep the serial link light.
	private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(10);
	private const int ChannelPollEveryTicks = 6;
	private int _pollTick;

	// Pull the channel list once the radio first answers; re-pulled on the channel_info control.
	private bool _channelsReported;

	// Channel number -> RX frequency label (e.g. "5940.0 kHz"), so the current-channel state report can
	// show the same label the channel picker uses.
	private readonly Dictionary<int, string> _channelRxLabels = new();

	// Latest polled power level (true = high). Drives the UI power button red state.
	private bool _highPower;

	private readonly IModuleHost _host;

	private JsonNode _config = new JsonObject();
	private Barrett2050Link? _link;
	private CancellationTokenSource? _lifetime;
	private Task? _pollTask;
	private string? _comPort;
	private int _baud = 9600;
	private int _lastChannel = -1;

	public Barrett2050Module(IModuleHost host)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
	}

	// ── Lifecycle ──────────────────────────────────────────────────────────────────────

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		ReadSettings();
		_lifetime = new CancellationTokenSource();
		_link = BuildLink();

		if (_link is null)
		{
			_host.Log(LogLevel.Warning, "Barrett 2050: no control transport (set 'com_port' or provide a shared CAT port). Running with no hardware control.");
			return;
		}

		// Make sure RS232 control is enabled before we issue commands (XO1, best effort).
		try
		{
			await _link.SendAsync("XO1", cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
		{
			_host.Log(LogLevel.Warning, $"Barrett 2050: could not enable RS232 control: {ex.Message}");
		}

		// Pull the channel list from the radio once at startup and report it (§3.11). Best effort: a radio
		// that does not answer IDL just leaves the list empty until the operator runs channel_info.
		await ReportChannelsAsync(cancellationToken).ConfigureAwait(false);
		_channelsReported = true;

		// Report the radio's CURRENT channel (IC) at boot so the UI immediately shows what it is tuned to,
		// without waiting for the first 25 s poll cycle.
		await ReportCurrentChannelAsync(cancellationToken).ConfigureAwait(false);

		_pollTask = Task.Run(() => PollLoopAsync(_lifetime.Token), CancellationToken.None);
		_host.Log(LogLevel.Info, $"Barrett 2050 module started ({_comPort ?? "shared CAT port"}).");
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		if (_lifetime is not null)
		{
			await _lifetime.CancelAsync().ConfigureAwait(false);
		}

		if (_pollTask is not null)
		{
			try
			{
				await _pollTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
			}
		}

		if (_link is not null)
		{
			await _link.DisposeAsync().ConfigureAwait(false);
			_link = null;
		}
	}

	public ValueTask DisposeAsync()
	{
		_lifetime?.Dispose();
		return ValueTask.CompletedTask;
	}

	// ── Config ─────────────────────────────────────────────────────────────────────────

	public Task<OperationResult> ApplyConfigAsync(JsonElement settings, CancellationToken cancellationToken)
	{
		_config = JsonNode.Parse(settings.GetRawText()) ?? new JsonObject();
		var previousPort = _comPort;
		var previousBaud = _baud;
		ReadSettings();

		// If the serial binding changed and we own the port, reopen it (only when already started).
		if (_lifetime is not null && (!string.Equals(previousPort, _comPort, StringComparison.Ordinal) || previousBaud != _baud))
		{
			_ = ReopenOwnedLinkAsync();
		}

		return Task.FromResult(OperationResult.Ok());
	}

	public JsonNode GetConfig() => _config.DeepClone();

	private void ReadSettings()
	{
		var settings = _config as JsonObject ?? new JsonObject();
		_comPort = (settings["com_port"]?.GetValue<string>() is { Length: > 0 } port) ? port : null;
		_baud = ReadBaud(settings);
	}

	// Baud must be a POSITIVE integer or SerialPort throws "Positive number required". The config editor can
	// leave it blank/0, and JSON may carry it as a number or string, so parse defensively and fall back to
	// the Barrett default (9600) on anything missing/invalid/non-positive.
	private static int ReadBaud(JsonObject settings)
	{
		var node = settings["baud"];
		if (node is null)
		{
			return 9600;
		}

		int baud;
		try
		{
			baud = node.GetValueKind() switch
			{
				JsonValueKind.Number => node.GetValue<int>(),
				JsonValueKind.String => int.TryParse(node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
				_ => 0,
			};
		}
		catch (Exception ex) when (ex is InvalidOperationException or FormatException)
		{
			baud = 0;
		}

		return baud > 0 ? baud : 9600;
	}

	private string? Selcall => (_config as JsonObject)?["selcall_id"]?.GetValue<string>();

	private async Task ReopenOwnedLinkAsync()
	{
		// Only reopen a port we own; a host-shared transport stays as the host manages it.
		if (_comPort is null)
		{
			return;
		}

		try
		{
			if (_link is not null)
			{
				await _link.DisposeAsync().ConfigureAwait(false);
			}

			_link = BuildLink();
		}
		catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
		{
			_host.Log(LogLevel.Error, $"Barrett 2050: could not reopen serial port '{_comPort}': {ex.Message}");
		}
	}

	private Barrett2050Link? BuildLink()
	{
		// Prefer our own configured serial port; otherwise use the AP's shared CAT transport.
		if (_comPort is not null)
		{
			try
			{
				return new Barrett2050Link(new SerialPortByteLink(_comPort, _baud));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
			{
				_host.Log(LogLevel.Error, $"Barrett 2050: could not open serial port '{_comPort}': {ex.Message}");
				return null;
			}
		}

		return _host.ControlTransport is { } transport ? new Barrett2050Link(new ControlTransportByteLink(transport)) : null;
	}

	// ── Keying (PTT via Select Tx/rx) ────────────────────────────────────────────────────

	public Task KeyAsync(CancellationToken cancellationToken) => SendBoolCommandAsync("XP1", cancellationToken);

	public Task UnkeyAsync(CancellationToken cancellationToken) => SendBoolCommandAsync("XP0", cancellationToken);

	private async Task SendBoolCommandAsync(string command, CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return;
		}

		var response = await _link.SendAsync(command, cancellationToken).ConfigureAwait(false);
		if (!Barrett2050Link.IsOk(response))
		{
			_host.Log(LogLevel.Warning, $"Barrett 2050: '{command}' returned '{response}'.");
		}
	}

	// ── Controls ─────────────────────────────────────────────────────────────────────────

	public async Task<OperationResult> ExecuteControlAsync(string action, JsonElement args, CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return OperationResult.Error("Barrett 2050: no control transport available.");
		}

		switch (action)
		{
			case "channel_select":
				if (!TryGetInt(args, "channel", out var channel) || channel < 1 || channel > 9999)
				{
					return Rejected("channel", "Channel must be an integer 1-9999.");
				}

				return await ExecuteAsync($"XC{channel}", cancellationToken).ConfigureAwait(false);

			case "scan":
				// { "state": "start"|"stop" }  or  { "on": true|false }
				bool start = (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String)
					? string.Equals(s.GetString(), "start", StringComparison.OrdinalIgnoreCase)
					: args.ValueKind == JsonValueKind.Object && args.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
				return await ExecuteAsync(start ? "XN1" : "XN0", cancellationToken).ConfigureAwait(false);

			case "channel_info":
				// Return all channel use labels (IDL): collect the multi-line reply, emit it, and report the
				// channel list to the AP so the UI's channel picker is populated (§3.11).
				await ReportChannelsAsync(cancellationToken).ConfigureAwait(false);
				return OperationResult.Ok();

			default:
				return OperationResult.Error($"Control '{action}' is not supported by the Barrett 2050.");
		}
	}

	// ── Function buttons (§3.10) ──────────────────────────────────────────────────────────
	// Press acks promptly; menu buttons run their session in the background (§3.10.2).

	public async Task<OperationResult> PressButtonAsync(string buttonId, string consoleId, CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return OperationResult.Error("Barrett 2050: no control transport available.");
		}

		switch (buttonId)
		{
			case "selcall":
				_ = RunSelcallMenuAsync(consoleId);
				return OperationResult.Ok();
			case "pagecall":
				_ = RunPagecallMenuAsync(consoleId);
				return OperationResult.Ok();
			case "clarifier":
				_ = RunClarifierMenuAsync(consoleId);
				return OperationResult.Ok();
			case "power":
				// Direct toggle (not a menu): high <-> low, then re-read so the red state reflects reality.
				return await TogglePowerAsync(cancellationToken).ConfigureAwait(false);
			default:
				return OperationResult.Error($"Unknown function button '{buttonId}'.");
		}
	}

	// Toggles transmit power high<->low (EHH/EHL). Reads the current level (IH), sends the opposite, re-reads
	// and reports so the power button's red (high) state updates immediately.
	private async Task<OperationResult> TogglePowerAsync(CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return OperationResult.Error("Barrett 2050: no control transport available.");
		}

		var current = ParsePowerLevel(await _link.SendAsync("IH", cancellationToken).ConfigureAwait(false));
		var goHigh = current != 'H';   // if currently high, drop to low; otherwise go high
		var setCmd = goHigh ? "EHH" : "EHL";
		await _link.SendAsync(setCmd, cancellationToken).ConfigureAwait(false);

		var after = ParsePowerLevel(await _link.SendAsync("IH", cancellationToken).ConfigureAwait(false));
		if (after == 'M')
		{
			await _link.SendAsync("EHH", cancellationToken).ConfigureAwait(false);
			after = ParsePowerLevel(await _link.SendAsync("IH", cancellationToken).ConfigureAwait(false));
		}

		_highPower = after == 'H';
		_host.Log(LogLevel.Info, $"Barrett 2050 power toggle: was {current}, sent {setCmd}, now {after} (high={_highPower}).");
		_host.ReportState(new RadioStateReport(
			Channel: CurrentChannelInfo(),
			Zone: null,
			Mode: null,
			Signal: null,
			Ready: true,
			Buttons: BuildButtonStates()));
		return OperationResult.Ok();
	}

	private async Task RunSelcallMenuAsync(string consoleId)
	{
		var token = _lifetime?.Token ?? CancellationToken.None;
		const string schema = """
			{ "type":"object",
			  "properties": { "destination": { "type":"string", "title":"Selcall ID (4 or 6 digits)", "pattern":"^[0-9]{4}([0-9]{2})?$" } },
			  "required":["destination"] }
			""";

		var result = await _host.ShowMenuAsync(consoleId, new MenuSpec("Transmit Selcall", schema), token).ConfigureAwait(false);
		if (result is { Submitted: true, Values: not null } && result.Values["destination"]?.GetValue<string>() is { Length: > 0 } destination)
		{
			await ExecuteAndLogAsync($"XZN{destination}", token).ConfigureAwait(false);
		}
	}

	private async Task RunPagecallMenuAsync(string consoleId)
	{
		var token = _lifetime?.Token ?? CancellationToken.None;
		const string schema = """
			{ "type":"object",
			  "properties": {
			    "destination": { "type":"string", "title":"Selcall ID (4 or 6 digits)", "pattern":"^[0-9]{4}([0-9]{2})?$" },
			    "message":     { "type":"string", "title":"Message", "maxLength":32 } },
			  "required":["destination","message"] }
			""";

		var result = await _host.ShowMenuAsync(consoleId, new MenuSpec("Transmit Pagecall", schema), token).ConfigureAwait(false);
		if (result is { Submitted: true, Values: not null }
			&& result.Values["destination"]?.GetValue<string>() is { Length: > 0 } destination
			&& result.Values["message"]?.GetValue<string>() is { } message)
		{
			await ExecuteAndLogAsync($"XZM{destination}M{message}", token).ConfigureAwait(false);
		}
	}

	private async Task RunClarifierMenuAsync(string consoleId)
	{
		var token = _lifetime?.Token ?? CancellationToken.None;

		// Get-current: IF returns sign + 4-digit HEX Hz (e.g. "+0064" = +100 Hz).
		int currentHz = 0;
		if (_link is not null)
		{
			var current = await _link.SendAsync("IF", token).ConfigureAwait(false);
			currentHz = ParseClarifierHz(current);
		}

		const string schema = """
			{ "type":"object",
			  "properties": { "clarifier_hz": { "type":"integer", "title":"Clarifier (Hz)", "minimum":-1000, "maximum":1000 } },
			  "required":["clarifier_hz"] }
			""";
		var initial = new JsonObject { ["clarifier_hz"] = currentHz };

		var result = await _host.ShowMenuAsync(consoleId, new MenuSpec("Clarifier", schema, initial), token).ConfigureAwait(false);
		if (result is { Submitted: true, Values: not null } && result.Values["clarifier_hz"]?.GetValue<int>() is int hz)
		{
			hz = Math.Clamp(hz, -1000, 1000);
			// Set (EF) uses sign + 4-digit DECIMAL Hz.
			var sign = hz < 0 ? "-" : "+";
			await ExecuteAndLogAsync($"EF{sign}{Math.Abs(hz):0000}", token).ConfigureAwait(false);
		}
	}

	private async Task RunPowerMenuAsync(string consoleId)
	{
		var token = _lifetime?.Token ?? CancellationToken.None;

		// Get-current: IH returns L / M / H.
		string current = "medium";
		if (_link is not null)
		{
			var ih = await _link.SendAsync("IH", token).ConfigureAwait(false);
			current = ih.Trim().ToUpperInvariant() switch { "H" => "high", "L" => "low", _ => "medium" };
		}

		const string schema = """
			{ "type":"object",
			  "properties": { "level": { "type":"string", "title":"Transmit power", "enum":["low","medium","high"] } },
			  "required":["level"] }
			""";
		var initial = new JsonObject { ["level"] = current };

		var result = await _host.ShowMenuAsync(consoleId, new MenuSpec("Power Level", schema, initial), token).ConfigureAwait(false);
		if (result is { Submitted: true, Values: not null } && result.Values["level"]?.GetValue<string>() is { } level)
		{
			var code = level.ToLowerInvariant() switch { "high" => "H", "low" => "L", _ => "M" };
			await ExecuteAndLogAsync($"EH{code}", token).ConfigureAwait(false);
		}
	}

	// ── State polling ─────────────────────────────────────────────────────────────────────

	private async Task PollLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await PollOnceAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
			{
				_host.Log(LogLevel.Debug, $"Barrett 2050 poll error: {ex.Message}");
			}

			try
			{
				await Task.Delay(StatusPollInterval, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	private async Task PollOnceAsync(CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return;
		}

		// Each datum is its own spaced transaction (the link drains + spaces). We do NOT hard-gate on IV:
		// this radio does not answer IV even though it answers IE/IDF/IC/IS/IH, so gating on IV wrongly
		// declared it offline and suppressed scan/power and wiped the channel. Online = it answered ANYTHING.
		var iv = await SendLoggedAsync("IV", cancellationToken).ConfigureAwait(false);

		// Pull the programmed channel list once we know the radio is talking (any reply, incl. from below).
		if (!_channelsReported && Responded(iv))
		{
			await ReportChannelsAsync(cancellationToken).ConfigureAwait(false);
			_channelsReported = true;
		}

		// Current channel (IC): only every ChannelPollEveryTicks (60s); the cached value rides every status
		// report so the 10s status polls never blank the displayed channel.
		string? icReply = null;
		if (_pollTick % ChannelPollEveryTicks == 0)
		{
			icReply = await SendLoggedAsync("IC", cancellationToken).ConfigureAwait(false);
			if (int.TryParse(icReply.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelNumber) && channelNumber > 0)
			{
				_lastChannel = channelNumber;
			}
		}

		_pollTick++;

		// Scan state: "IS" -> Y = scanning, N or E0 = not scanning.
		var scanText = await SendLoggedAsync("IS", cancellationToken).ConfigureAwait(false);
		bool? scanning = ParseScanState(scanText);

		// Power level: "IH" -> L = low, H = high, M = medium. We never run at medium: if the radio reports
		// M, force high (EHH) and re-read so the state reflects the corrected level. UI shows red when high.
		var powerText = await SendLoggedAsync("IH", cancellationToken).ConfigureAwait(false);
		var power = ParsePowerLevel(powerText);
		if (power == 'M')
		{
			await SendLoggedAsync("EHH", cancellationToken).ConfigureAwait(false);
			powerText = await SendLoggedAsync("IH", cancellationToken).ConfigureAwait(false);
			power = ParsePowerLevel(powerText);
		}

		_highPower = power == 'H';

		// Pull the channel list lazily if IV stayed silent but other queries answered (radio is clearly up).
		if (!_channelsReported && (Responded(scanText) || Responded(powerText)))
		{
			await ReportChannelsAsync(cancellationToken).ConfigureAwait(false);
			_channelsReported = true;
		}

		var online = Responded(iv) || Responded(icReply) || Responded(scanText) || Responded(powerText);
		if (!online)
		{
			_channelsReported = false;   // re-pull the channel list when it comes back
			_host.ReportState(new RadioStateReport(Channel: null, Zone: null, Mode: null, Signal: null, Ready: false));
			return;
		}

		_host.ReportState(new RadioStateReport(
			Channel: CurrentChannelInfo(),
			Zone: null,
			Mode: null,
			Signal: null,
			Ready: true,
			Scan: scanning,
			Buttons: BuildButtonStates()));
	}

	// A non-empty reply means the radio responded (even an "E0" error means it is present); empty = timeout.
	private static bool Responded(string? reply) => !string.IsNullOrWhiteSpace(reply);

	// Sends a command and logs its raw reply for diagnosis (each command is a separate spaced transaction).
	private async Task<string> SendLoggedAsync(string command, CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return string.Empty;
		}

		var reply = await _link.SendAsync(command, cancellationToken).ConfigureAwait(false);
		_host.Log(LogLevel.Info, $"Barrett 2050 {command} -> '{reply.Trim()}'");
		return reply;
	}

	// The cached current channel built into every state report (so 10s status polls don't blank it). Null
	// until the first IC read succeeds.
	private ChannelInfo? CurrentChannelInfo()
		=> _lastChannel > 0
			? new ChannelInfo(_lastChannel, _channelRxLabels.TryGetValue(_lastChannel, out var label) ? label : null)
			: null;

	// Reads the current channel (IC) and caches it in _lastChannel; no report (the caller reports).
	private async Task UpdateCurrentChannelAsync(CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return;
		}

		var channelText = await _link.SendAsync("IC", cancellationToken).ConfigureAwait(false);
		if (int.TryParse(channelText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelNumber) && channelNumber > 0)
		{
			_lastChannel = channelNumber;
		}
	}

	// Live function-button states reported with every state update: the power button is "active" (red in the
	// UI) while the radio is on high power.
	private IReadOnlyDictionary<string, RadioButtonStateReport> BuildButtonStates()
		=> new Dictionary<string, RadioButtonStateReport> { ["power"] = new RadioButtonStateReport(Active: _highPower) };

	// "IS" scan reply: Y = scanning; N or E0 (or anything else) = not scanning. Tolerates an "IS" echo.
	private static bool? ParseScanState(string reply)
	{
		var r = reply.Trim().ToUpperInvariant();
		if (r.StartsWith("IS", StringComparison.Ordinal))
		{
			r = r[2..].Trim();
		}

		if (r.StartsWith("Y", StringComparison.Ordinal))
		{
			return true;
		}

		return r.Length == 0 ? (bool?)null : false;
	}

	// "IH" power reply -> 'H' (high), 'L' (low), 'M' (medium), or '?' (unknown/error). Tolerates an "IH"
	// echo prefix (e.g. "IHH").
	private static char ParsePowerLevel(string reply)
	{
		var r = reply.Trim().ToUpperInvariant();
		if (r.StartsWith("IH", StringComparison.Ordinal))
		{
			r = r[2..].Trim();
		}

		foreach (var c in r)
		{
			if (c is 'H' or 'L' or 'M')
			{
				return c;
			}
		}

		return '?';
	}

	// Reads the radio's current channel (IC) and reports it, so the UI shows the tuned channel immediately
	// (e.g. at boot) without waiting for the poll loop. Labels the channel with its RX frequency if known.
	private async Task ReportCurrentChannelAsync(CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return;
		}

		try
		{
			await UpdateCurrentChannelAsync(cancellationToken).ConfigureAwait(false);
			if (_lastChannel <= 0)
			{
				return;
			}

			_host.ReportState(new RadioStateReport(
				Channel: CurrentChannelInfo(),
				Zone: null,
				Mode: null,
				Signal: null,
				Ready: true,
				Buttons: BuildButtonStates()));
		}
		catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
		{
			_host.Log(LogLevel.Debug, $"Barrett 2050: could not read current channel (IC): {ex.Message}");
		}
	}

	// Pull the programmed channel list from the radio and report it to the AP, which publishes it on
	// module/<id>/channels for the UI's channel picker (§3.11). Protocol:
	//   IE  -> number of programmed channels.
	//   IDF -> frequency records, 20 ASCII digits each: channel(4) + RX Hz(8) + TX Hz(8). The channel list
	//          displays the RECEIVE frequency in kHz (e.g. 5940000 Hz -> "5940.0 kHz").
	private async Task ReportChannelsAsync(CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return;
		}

		try
		{
			// IE: programmed-channel count (used to bound/validate the IDF parse and the collect window).
			var countText = await _link.SendAsync("IE", cancellationToken).ConfigureAwait(false);
			_ = int.TryParse(new string(countText.Where(char.IsDigit).ToArray()), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelCount);

			// IDF: all programmed channels' frequency records, back to back. Allow a longer window for a
			// large codeplug.
			var raw = await _link.SendCollectAsync("IDF", TimeSpan.FromSeconds(channelCount > 60 ? 4 : 2), cancellationToken).ConfigureAwait(false);
			var channels = ParseChannelFrequencies(raw);
			if (channels.Count == 0)
			{
				_host.Log(LogLevel.Debug, $"Barrett 2050: IDF returned no parseable channel records (IE reported {channelCount}).");
				return;
			}

			// Remember each channel's RX label so the current-channel state report can show it too.
			_channelRxLabels.Clear();
			foreach (var entry in channels)
			{
				_channelRxLabels[entry.Index] = entry.Label!;
			}

			_host.ReportChannels(channels);
			_host.Log(LogLevel.Info, $"Barrett 2050: reported {channels.Count} channel(s) (IE count {channelCount}).");
		}
		catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
		{
			_host.Log(LogLevel.Debug, $"Barrett 2050: could not pull channel list (IE/IDF): {ex.Message}");
		}
	}

	// Parse concatenated IDF frequency records (20 ASCII digits each: channel(4) + RX Hz(8) + TX Hz(8)).
	// Returns one ChannelInfo per channel, labelled with the RECEIVE frequency in kHz.
	private static IReadOnlyList<ChannelInfo> ParseChannelFrequencies(string raw)
	{
		// Keep only digits so CRs, spaces, or framing between records don't break the fixed-width parse.
		var digits = new string((raw ?? string.Empty).Where(char.IsDigit).ToArray());
		const int recordLength = 20;
		var channels = new List<ChannelInfo>(digits.Length / recordLength);

		for (var offset = 0; offset + recordLength <= digits.Length; offset += recordLength)
		{
			var record = digits.AsSpan(offset, recordLength);
			if (!int.TryParse(record[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelNumber)
				|| !long.TryParse(record.Slice(4, 8), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rxHz))
			{
				continue;
			}

			if (channelNumber == 0 && rxHz == 0)
			{
				continue;   // padding / empty record
			}

			// RX frequency Hz -> kHz with one decimal, e.g. 5940000 -> "5940.0 kHz".
			var rxLabel = string.Create(CultureInfo.InvariantCulture, $"{rxHz / 1000.0:0.0} kHz");
			channels.Add(new ChannelInfo(channelNumber, rxLabel));
		}

		return channels;
	}

	// An interrogate reply of the form "Exx" (e.g. "E01") is an error, not data.
	private static bool IsErrorReply(string reply)
	{
		reply = reply.Trim();
		return reply.Length >= 2 && (reply[0] == 'E' || reply[0] == 'e') && char.IsDigit(reply[1]);
	}

	// ── Helpers ───────────────────────────────────────────────────────────────────────────

	private async Task<OperationResult> ExecuteAsync(string command, CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return OperationResult.Error("Barrett 2050: no control transport available.");
		}

		var response = await _link.SendAsync(command, cancellationToken).ConfigureAwait(false);
		return Barrett2050Link.IsOk(response)
			? OperationResult.Ok()
			: OperationResult.Error($"Barrett 2050 returned '{response}' for '{command}'.");
	}

	private async Task ExecuteAndLogAsync(string command, CancellationToken cancellationToken)
	{
		var result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
		if (result.Status != OperationStatus.Ok)
		{
			_host.Log(LogLevel.Warning, result.Errors?.FirstOrDefault()?.Message ?? $"Barrett 2050: '{command}' failed.");
		}
	}

	private static OperationResult Rejected(string field, string message) =>
		OperationResult.Rejected([new FieldError(field, "invalid", message)]);

	private static bool TryGetInt(JsonElement args, string property, out int value)
	{
		value = 0;
		return args.ValueKind == JsonValueKind.Object
			&& args.TryGetProperty(property, out var element)
			&& element.TryGetInt32(out value);
	}

	// Parse "+0064" (sign + 4 hex digits) into signed Hz.
	private static int ParseClarifierHz(string response)
	{
		response = response.Trim();
		if (response.Length < 2)
		{
			return 0;
		}

		var sign = response[0] == '-' ? -1 : 1;
		var digits = (response[0] is '+' or '-') ? response[1..] : response;
		return int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var magnitude) ? sign * magnitude : 0;
	}
}
