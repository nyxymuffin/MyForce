// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// PROJECT_FRAMEWORK.md Phase 1: the relay keying primitive (§3.6.3). The AP's first built-in:
// it drives a channel on an AP-owned RS232 relay board DIRECTLY (never via the GPIO Relay
// Controller) so keying stays co-located with the audio gate (§3.4). A relay set is one
// serial port with a single owner (§3.6.3); multiple sets are supported.
//
// Board command dialects vary, so the protocol is configurable. A set with no COM port (or
// "virtual") is an in-memory relay, which keeps the TX path exercisable on a dev box with no
// relay hardware; a real port that fails to open marks the set Unavailable so key() fails and
// the TX state machine aborts per §3.6.9.

using System.IO.Ports;

/// <summary>Relay board command dialect so the AP knows what bytes to send (§3.6.3).</summary>
internal enum RelayBoardProtocol
{
	/// <summary>ASCII line commands: "ON{ch}\r\n" / "OFF{ch}\r\n".</summary>
	Ascii,

	/// <summary>Three-byte command common to USB-serial relay boards: {0xFF, channel, state}.</summary>
	NumericByte,

	/// <summary>Active-low ASCII: assert closes the line, so the on/off words are swapped.</summary>
	ActiveLowAscii
}

/// <summary>
/// Owns the AP's relay sets and exposes assert/deassert by (relay set id, channel). The TX
/// state machine (§3.4) and 4W Resource (§3.6.2) key through this; the GPIO controller never
/// does (§3.6.3).
/// </summary>
internal sealed class RelayKeyingService : IDisposable
{
	private readonly Dictionary<string, RelaySetController> _sets;
	private readonly Action<string, string> _log;

	public RelayKeyingService(IReadOnlyList<AudioProcessorCoordinator.RelaySetDefinition> relaySets, Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(relaySets);
		ArgumentNullException.ThrowIfNull(log);
		_log = log;
		_sets = relaySets.ToDictionary(
			static set => set.RelaySetId,
			set => new RelaySetController(set, log),
			StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Assert (key) a relay channel. Returns false if the set's hardware is unavailable, which
	/// the caller treats as a key() failure and aborts the TX sequence (§3.6.9).
	/// </summary>
	public bool Assert(string relaySetId, int channel) => GetOrCreate(relaySetId).TrySetChannel(channel, true);

	/// <summary>Deassert (un-key) a relay channel.</summary>
	public bool Deassert(string relaySetId, int channel) => GetOrCreate(relaySetId).TrySetChannel(channel, false);

	/// <summary>True when the set has a usable owner (real open port or a virtual set).</summary>
	public bool IsSetAvailable(string relaySetId) => GetOrCreate(relaySetId).IsAvailable;

	private RelaySetController GetOrCreate(string relaySetId)
	{
		if (_sets.TryGetValue(relaySetId, out var controller))
		{
			return controller;
		}

		// A radio may reference a relay set that was never defined in admin (common in dev).
		// Synthesize a virtual set so keying is logically functional without hardware.
		_log("tx", $"Relay set '{relaySetId}' was not defined; using a virtual relay set so keying remains functional.");
		controller = new RelaySetController(new AudioProcessorCoordinator.RelaySetDefinition(relaySetId, string.Empty, 9600, "ascii", 8), _log);
		_sets[relaySetId] = controller;
		return controller;
	}

	public void Dispose()
	{
		foreach (var controller in _sets.Values)
		{
			controller.Dispose();
		}

		_sets.Clear();
	}

	/// <summary>One relay board: a single serial port with one owner (§3.6.3), or a virtual set.</summary>
	private sealed class RelaySetController : IDisposable
	{
		private readonly AudioProcessorCoordinator.RelaySetDefinition _definition;
		private readonly RelayBoardProtocol _protocol;
		private readonly Action<string, string> _log;
		private readonly bool _isVirtual;
		private readonly object _gate = new();

		private SerialPort? _port;
		private bool _openAttempted;

		public RelaySetController(AudioProcessorCoordinator.RelaySetDefinition definition, Action<string, string> log)
		{
			_definition = definition;
			_log = log;
			_protocol = ParseProtocol(definition.Protocol);
			_isVirtual = string.IsNullOrWhiteSpace(definition.ComPort)
				|| string.Equals(definition.ComPort, "virtual", StringComparison.OrdinalIgnoreCase);
		}

		public bool IsAvailable
		{
			get
			{
				if (_isVirtual)
				{
					return true;
				}

				EnsureOpen();
				return _port is { IsOpen: true };
			}
		}

		public bool TrySetChannel(int channel, bool asserted)
		{
			if (channel < 1 || channel > _definition.ChannelCount)
			{
				_log("tx", $"Relay set '{_definition.RelaySetId}' channel {channel} is out of range (1..{_definition.ChannelCount}).");
				return false;
			}

			if (_isVirtual)
			{
				_log("tx", $"Virtual relay set '{_definition.RelaySetId}' channel {channel} -> {(asserted ? "ASSERT" : "RELEASE")}.");
				return true;
			}

			lock (_gate)
			{
				EnsureOpen();
				if (_port is not { IsOpen: true })
				{
					return false;
				}

				try
				{
					WriteCommand(_port, channel, asserted);
					return true;
				}
				catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
				{
					_log("tx", $"Relay set '{_definition.RelaySetId}' write failed on channel {channel}: {ex.Message}.");
					return false;
				}
			}
		}

		private void EnsureOpen()
		{
			if (_openAttempted)
			{
				return;
			}

			_openAttempted = true;
			try
			{
				_port = new SerialPort(_definition.ComPort, _definition.Baud)
				{
					ReadTimeout = 500,
					WriteTimeout = 500
				};
				_port.Open();
				_log("tx", $"Opened relay set '{_definition.RelaySetId}' on {_definition.ComPort} @ {_definition.Baud} ({_protocol}).");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
			{
				_port?.Dispose();
				_port = null;
				_log("tx", $"Relay set '{_definition.RelaySetId}' on {_definition.ComPort} could not be opened: {ex.Message}. Set marked Unavailable.");
			}
		}

		private void WriteCommand(SerialPort port, int channel, bool asserted)
		{
			switch (_protocol)
			{
				case RelayBoardProtocol.NumericByte:
					port.Write(new byte[] { 0xFF, (byte)channel, (byte)(asserted ? 0x01 : 0x00) }, 0, 3);
					break;
				case RelayBoardProtocol.ActiveLowAscii:
					port.WriteLine(asserted ? $"OFF{channel}" : $"ON{channel}");
					break;
				case RelayBoardProtocol.Ascii:
				default:
					port.WriteLine(asserted ? $"ON{channel}" : $"OFF{channel}");
					break;
			}
		}

		private static RelayBoardProtocol ParseProtocol(string? protocol)
		{
			return protocol?.Trim().ToLowerInvariant() switch
			{
				"numericbyte" or "numeric" or "byte" => RelayBoardProtocol.NumericByte,
				"activelowascii" or "activelow" => RelayBoardProtocol.ActiveLowAscii,
				_ => RelayBoardProtocol.Ascii
			};
		}

		public void Dispose()
		{
			lock (_gate)
			{
				if (_port is { IsOpen: true })
				{
					try
					{
						_port.Close();
					}
					catch (IOException)
					{
					}
				}

				_port?.Dispose();
				_port = null;
			}
		}
	}
}
