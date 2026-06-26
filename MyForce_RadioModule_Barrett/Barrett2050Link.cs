// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// RS232 control link for the Barrett 2050 (per "2050 RS232 Control Manual-04"). Commands are
// ASCII verbs terminated with <CR>; the transceiver replies with a <CR>-terminated line:
// "OK"/"Okay" for execute/edit success, "Exx" on error, or the requested data for interrogate
// commands. This layer serialises request/response and reads up to the terminating <CR>.
using System.IO.Ports;
using System.Text;
using MyForce.Contracts.Radio;

namespace MyForce.RadioModules.Barrett;

/// <summary>Minimal byte transport: either the AP-owned shared CAT port or our own serial port.</summary>
internal interface IByteLink : IAsyncDisposable
{
	Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

	ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>Adapts the host's shared <see cref="IControlTransport"/> (AP owns the port).</summary>
internal sealed class ControlTransportByteLink : IByteLink
{
	private readonly IControlTransport _transport;

	public ControlTransportByteLink(IControlTransport transport) => _transport = transport;

	public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
		=> _transport.WriteAsync(data, cancellationToken);

	public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
		=> await _transport.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

	// The host owns the shared transport's lifetime; nothing to release here.
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Opens and owns a dedicated serial port for the Barrett CAT/keying line.</summary>
internal sealed class SerialPortByteLink : IByteLink
{
	private readonly SerialPort _port;

	public SerialPortByteLink(string portName, int baud)
	{
		_port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
		{
			ReadTimeout = 500,
			WriteTimeout = 500,
			Handshake = Handshake.None,
		};
		_port.Open();
	}

	public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
		=> _port.BaseStream.WriteAsync(data, cancellationToken).AsTask();

	public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
		=> _port.BaseStream.ReadAsync(buffer, cancellationToken);

	public ValueTask DisposeAsync()
	{
		try
		{
			if (_port.IsOpen)
			{
				_port.Close();
			}

			_port.Dispose();
		}
		catch (IOException)
		{
			// Best-effort close.
		}

		return ValueTask.CompletedTask;
	}
}

/// <summary>
/// Command layer over an <see cref="IByteLink"/>: send a verb (CR appended) and read the
/// CR-terminated reply. Request/response is serialised so concurrent commands don't interleave.
/// </summary>
internal sealed class Barrett2050Link : IAsyncDisposable
{
	private const byte CarriageReturn = (byte)'\r';

	private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(1500);

	private readonly IByteLink _link;
	private readonly SemaphoreSlim _gate = new(1, 1);

	public Barrett2050Link(IByteLink link) => _link = link;

	/// <summary>Send a command (CR appended) and return the trimmed CR-terminated reply.</summary>
	public async Task<string> SendAsync(string command, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var payload = Encoding.ASCII.GetBytes(command + "\r");
			await _link.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
			return await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task<string> ReadResponseAsync(CancellationToken cancellationToken)
	{
		var builder = new StringBuilder();
		var readBuffer = new byte[64];

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(ResponseTimeout);

		try
		{
			while (true)
			{
				int read = await _link.ReadAsync(readBuffer, timeoutCts.Token).ConfigureAwait(false);
				if (read <= 0)
				{
					break;
				}

				for (int i = 0; i < read; i++)
				{
					byte b = readBuffer[i];
					if (b == CarriageReturn)
					{
						return builder.ToString().Trim();
					}

					if (b != (byte)'\n')
					{
						builder.Append((char)b);
					}
				}
			}
		}
		catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			// Response timeout, return whatever (if anything) arrived.
		}

		return builder.ToString().Trim();
	}

	/// <summary>
	/// Send a command and collect ALL reply bytes for a fixed window, for multi-line replies such as
	/// "Return all channel use labels" (IDL) whose entries are not a single CR-terminated line.
	/// </summary>
	public async Task<string> SendCollectAsync(string command, TimeSpan window, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var payload = Encoding.ASCII.GetBytes(command + "\r");
			await _link.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

			var builder = new StringBuilder();
			var readBuffer = new byte[256];
			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutCts.CancelAfter(window);

			try
			{
				while (true)
				{
					int read = await _link.ReadAsync(readBuffer, timeoutCts.Token).ConfigureAwait(false);
					if (read <= 0)
					{
						break;
					}

					builder.Append(Encoding.ASCII.GetString(readBuffer, 0, read));
				}
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				// Collection window elapsed.
			}

			return builder.ToString();
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>True when an execute/edit reply indicates success ("OK" or "Okay").</summary>
	public static bool IsOk(string response) => response.StartsWith("OK", StringComparison.OrdinalIgnoreCase);

	public ValueTask DisposeAsync() => _link.DisposeAsync();
}
