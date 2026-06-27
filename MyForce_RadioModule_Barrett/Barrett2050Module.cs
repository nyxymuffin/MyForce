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

	public string Version => "0.2.0";

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
			new FunctionButton("power", "Power", OpensMenu: true, Order: 4),
		]);

	public IRadioModule Create(IModuleHost host) => new Barrett2050Module(host);
}

/// <summary>Runtime behaviour for a single Barrett 2050 instance, keyed via RS232 Select Tx/rx.</summary>
public sealed class Barrett2050Module : IRadioModule, IKeyingProvider
{
	// Poll the radio every 25 s with "IV" to detect whether it is online (any reply = present).
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(25);

	// Pull the channel list once the radio first answers; re-pulled on the channel_info control.
	private bool _channelsReported;

	// Channel number -> RX frequency label (e.g. "5940.0 kHz"), so the current-channel state report can
	// show the same label the channel picker uses.
	private readonly Dictionary<int, string> _channelRxLabels = new();

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
		_baud = settings["baud"]?.GetValue<int>() ?? 9600;
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

	public Task<OperationResult> PressButtonAsync(string buttonId, string consoleId, CancellationToken cancellationToken)
	{
		if (_link is null)
		{
			return Task.FromResult(OperationResult.Error("Barrett 2050: no control transport available."));
		}

		switch (buttonId)
		{
			case "selcall":
				_ = RunSelcallMenuAsync(consoleId);
				return Task.FromResult(OperationResult.Ok());
			case "pagecall":
				_ = RunPagecallMenuAsync(consoleId);
				return Task.FromResult(OperationResult.Ok());
			case "clarifier":
				_ = RunClarifierMenuAsync(consoleId);
				return Task.FromResult(OperationResult.Ok());
			case "power":
				_ = RunPowerMenuAsync(consoleId);
				return Task.FromResult(OperationResult.Ok());
			default:
				return Task.FromResult(OperationResult.Error($"Unknown function button '{buttonId}'."));
		}
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
				await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
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

		// Online detection: "IV" (interrogate version/identity). Any non-error reply means the radio is
		// present and responding; a timeout/blank means it is offline.
		var iv = await _link.SendAsync("IV", cancellationToken).ConfigureAwait(false);
		var online = !string.IsNullOrWhiteSpace(iv) && !IsErrorReply(iv);
		if (!online)
		{
			_channelsReported = false;   // re-pull the channel list when it comes back
			_host.ReportState(new RadioStateReport(Channel: null, Zone: null, Mode: null, Signal: null, Ready: false));
			return;
		}

		// First reply after coming online: pull the programmed channel list (IE count + IDF frequencies).
		if (!_channelsReported)
		{
			await ReportChannelsAsync(cancellationToken).ConfigureAwait(false);
			_channelsReported = true;
		}

		// Current channel (IC, 4-digit channel number) + scan state (IS: Y/N) for the operating display.
		var channelText = await _link.SendAsync("IC", cancellationToken).ConfigureAwait(false);
		ChannelInfo? channel = int.TryParse(channelText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelNumber)
			? new ChannelInfo(channelNumber, _channelRxLabels.TryGetValue(channelNumber, out var rxLabel) ? rxLabel : null)
			: null;
		if (channel is not null)
		{
			_lastChannel = channel.Index;
		}

		var scanText = await _link.SendAsync("IS", cancellationToken).ConfigureAwait(false);
		bool? scanning = scanText.Trim().ToUpperInvariant() switch { "Y" => true, "N" => false, _ => (bool?)null };

		_host.ReportState(new RadioStateReport(
			Channel: channel,
			Zone: null,
			Mode: null,
			Signal: null,
			Ready: true,
			Scan: scanning));
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
