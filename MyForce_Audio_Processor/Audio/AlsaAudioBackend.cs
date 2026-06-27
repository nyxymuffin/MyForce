// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// PROJECT_FRAMEWORK.md Phase 1: the Linux ALSA backend implementing IAudioBackend (§3.6.4).
// ALSA is the deterministic, minimal-footprint path for the constrained in-vehicle box; the
// PipeWire backend (default) can be added behind the same interface without touching the
// matrix. The engine targets FLOAT mono at the engine rate; each radio soundcard, the mic,
// and the speaker map to one ALSA PCM device.
//
// The P/Invoke surface binds to libasound at runtime, so this compiles on the Windows dev
// box but only opens devices on Linux. Frame I/O is blocking readi/writei with xrun recovery;
// the loop is paced by a monotonic frame clock so multiple independent device clocks cannot
// stall each other. Concrete hardware tuning (period/buffer sizing, PipeWire graph routing)
// is follow-up work on the target.

using System.Diagnostics;
using System.Runtime.InteropServices;

internal sealed class AlsaAudioBackend : IAudioBackend
{
	private const string Lib = "libasound.so.2";

	// snd_pcm_stream_t
	private const int SND_PCM_STREAM_PLAYBACK = 0;
	private const int SND_PCM_STREAM_CAPTURE = 1;

	// snd_pcm_format_t: 32-bit float little-endian matches the engine's float frames.
	private const int SND_PCM_FORMAT_FLOAT_LE = 14;

	// snd_pcm_access_t
	private const int SND_PCM_ACCESS_RW_INTERLEAVED = 3;

	private readonly Action<string, string> _log;
	private readonly Stopwatch _clock = Stopwatch.StartNew();

	private AlsaPort[] _capturePorts = Array.Empty<AlsaPort>();
	private AlsaPort[] _playbackPorts = Array.Empty<AlsaPort>();
	private long _frameIndex;
	private bool _isRunning;
	private bool _isDisposed;

	public AlsaAudioBackend(EngineAudioFormat format, Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(log);
		Format = format;
		_log = log;
	}

	public string Name => "ALSA";

	public EngineAudioFormat Format { get; }

	public event EventHandler<AudioDeviceHotplugEventArgs>? DeviceHotplug;

	public IReadOnlyList<AudioBackendDevice> EnumerateDevices()
	{
		// Device discovery for the admin picker is already handled at a higher level via
		// AudioFrameworkCatalog.DiscoverPlaybackDevices(); ALSA card enumeration can be added
		// here later. Returning empty keeps that single source of truth.
		return Array.Empty<AudioBackendDevice>();
	}

	public AudioBackendBinding Bind(IReadOnlyList<string> captureDeviceIds, IReadOnlyList<string> playbackDeviceIds)
	{
		ArgumentNullException.ThrowIfNull(captureDeviceIds);
		ArgumentNullException.ThrowIfNull(playbackDeviceIds);

		_capturePorts = captureDeviceIds.Select(id => new AlsaPort(id, SND_PCM_STREAM_CAPTURE)).ToArray();
		_playbackPorts = playbackDeviceIds.Select(id => new AlsaPort(id, SND_PCM_STREAM_PLAYBACK)).ToArray();

		return new AudioBackendBinding(
			Array.AsReadOnly(_capturePorts.Select(static _ => false).ToArray()),
			Array.AsReadOnly(_playbackPorts.Select(static _ => false).ToArray()));
	}

	public void Start()
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);

		foreach (var port in _capturePorts)
		{
			TryOpenPort(port);
		}

		foreach (var port in _playbackPorts)
		{
			TryOpenPort(port);
		}

		_clock.Restart();
		_frameIndex = 0;
		_isRunning = true;
	}

	public void Stop()
	{
		_isRunning = false;
		foreach (var port in _capturePorts.Concat(_playbackPorts))
		{
			ClosePort(port);
		}
	}

	public bool WaitNextFrame()
	{
		if (!Volatile.Read(ref _isRunning))
		{
			return false;
		}

		// Monotonic frame clock: keeps the matrix tick steady regardless of per-device drift.
		var dueAt = TimeSpan.FromTicks((++_frameIndex) * Format.FrameDuration.Ticks);
		var remaining = dueAt - _clock.Elapsed;
		if (remaining > TimeSpan.Zero)
		{
			Thread.Sleep(remaining);
		}

		return Volatile.Read(ref _isRunning);
	}

	public void ReadCapture(int captureIndex, Span<float> frame)
	{
		if (captureIndex < 0 || captureIndex >= _capturePorts.Length)
		{
			frame.Clear();
			return;
		}

		var port = _capturePorts[captureIndex];
		if (port.Handle == IntPtr.Zero)
		{
			frame.Clear();
			return;
		}

		ReadInterleaved(port, frame);
	}

	public void WritePlayback(int playbackIndex, ReadOnlySpan<float> frame)
	{
		if (playbackIndex < 0 || playbackIndex >= _playbackPorts.Length)
		{
			return;
		}

		var port = _playbackPorts[playbackIndex];
		if (port.Handle == IntPtr.Zero)
		{
			return;
		}

		WriteInterleaved(port, frame);
	}

	public bool IsPortAvailable(AudioPortDirection direction, int index)
	{
		var ports = direction == AudioPortDirection.Capture ? _capturePorts : _playbackPorts;
		return index >= 0 && index < ports.Length && ports[index].Handle != IntPtr.Zero;
	}

	// Live re-bind (§3.7.8): runs on the RT thread, so closing the old PCM and opening the new one is
	// safe against ReadCapture/WritePlayback (same thread). A brief one-frame stall on reconfigure is
	// acceptable. No-op if the port already points at this device and is open.
	public void RebindPort(AudioPortDirection direction, int index, string deviceId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
		var ports = direction == AudioPortDirection.Capture ? _capturePorts : _playbackPorts;
		if (index < 0 || index >= ports.Length)
		{
			return;
		}

		var existing = ports[index];
		if (string.Equals(existing.DeviceId, deviceId, StringComparison.Ordinal) && existing.Handle != IntPtr.Zero)
		{
			return;
		}

		ClosePort(existing);
		var streamType = direction == AudioPortDirection.Capture ? SND_PCM_STREAM_CAPTURE : SND_PCM_STREAM_PLAYBACK;
		var port = new AlsaPort(deviceId, streamType);
		ports[index] = port;
		_log("engine", $"Re-binding {DescribeStream(streamType)} port {index} to '{deviceId}'.");
		if (_isRunning)
		{
			TryOpenPort(port);
		}
	}

	/// <summary>
	/// Translates a configured device id into an ALSA PCM name that can actually be opened on this host:
	///   - "alsa:hw:card,device"  -> raw ALSA "hw:card,device" (operator picked real hardware).
	///   - PipeWire/Pulse node    -> the ALSA->PipeWire bridge "pulse" PCM, targeting the specific node via
	///     PULSE_SINK/PULSE_SOURCE (raw node names like "alsa_input.usb-..." are NOT valid ALSA PCMs, which
	///     is why opening them directly failed and radio RX was never heard).
	///   - synthetic/logical name -> "default" (routes to the PipeWire default device).
	/// Returns the ALSA name plus an optional env var/value to set across the open so the bridge selects the
	/// intended node.
	/// </summary>
	private static (string AlsaName, string? EnvVar, string? EnvValue) ResolveAlsaTarget(string deviceId, bool isCapture)
	{
		if (deviceId.StartsWith("alsa:", StringComparison.OrdinalIgnoreCase))
		{
			return (deviceId["alsa:".Length..], null, null);
		}

		if (deviceId.StartsWith("alsa_", StringComparison.OrdinalIgnoreCase)
			|| deviceId.StartsWith("bluez", StringComparison.OrdinalIgnoreCase)
			|| deviceId.Contains('.'))
		{
			return ("pulse", isCapture ? "PULSE_SOURCE" : "PULSE_SINK", deviceId);
		}

		return ("default", null, null);
	}

	private void TryOpenPort(AlsaPort port)
	{
		var (alsaName, envVar, envValue) = ResolveAlsaTarget(port.DeviceId, port.StreamType == SND_PCM_STREAM_CAPTURE);
		var previousEnv = envVar is null ? null : Environment.GetEnvironmentVariable(envVar);
		if (envVar is not null)
		{
			Environment.SetEnvironmentVariable(envVar, envValue);
		}

		try
		{
			var result = snd_pcm_open(out var handle, alsaName, port.StreamType, 0);
			if (result < 0 || handle == IntPtr.Zero)
			{
				_log("engine", $"ALSA could not open '{port.DeviceId}' (as '{alsaName}'{(envVar is null ? string.Empty : $", {envVar}={envValue}")}, {DescribeStream(port.StreamType)}): {DescribeError(result)}. Port marked Unavailable.");
				return;
			}

			var paramsResult = snd_pcm_set_params(
				handle,
				SND_PCM_FORMAT_FLOAT_LE,
				SND_PCM_ACCESS_RW_INTERLEAVED,
				(uint)Format.Channels,
				(uint)Format.SampleRateHz,
				1, // allow ALSA resampling so a device at another native rate still binds (§3.6.5)
				100_000); // 100 ms target latency; tuned on hardware later

			if (paramsResult < 0)
			{
				_log("engine", $"ALSA parameter set failed for '{port.DeviceId}': {DescribeError(paramsResult)}. Closing port.");
				snd_pcm_close(handle);
				return;
			}

			port.Handle = handle;
			_log("engine", $"ALSA opened '{port.DeviceId}' (as '{alsaName}', {DescribeStream(port.StreamType)}).");
			DeviceHotplug?.Invoke(this, new AudioDeviceHotplugEventArgs(port.DeviceId, true));
		}
		catch (DllNotFoundException)
		{
			_log("engine", "libasound.so.2 is not present. ALSA backend is unavailable on this host; use the null backend for development.");
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_log("engine", $"ALSA open for '{port.DeviceId}' threw: {ex.Message}.");
		}
		finally
		{
			// Restore the global env we borrowed to target a specific PipeWire node for this open.
			if (envVar is not null)
			{
				Environment.SetEnvironmentVariable(envVar, previousEnv);
			}
		}
	}

	private void ReadInterleaved(AlsaPort port, Span<float> frame)
	{
		var frames = Format.FrameSamples;
		var read = snd_pcm_readi(port.Handle, ref MemoryMarshal.GetReference(frame), (ulong)frames);
		if (read < 0)
		{
			// Recover from xrun/suspend; on persistent failure emit silence for this frame.
			if (snd_pcm_recover(port.Handle, (int)read, 1) < 0)
			{
				frame.Clear();
			}
			else
			{
				frame.Clear();
			}

			return;
		}

		if (read < frames)
		{
			frame[(int)((long)read * Format.Channels)..].Clear();
		}
	}

	private void WriteInterleaved(AlsaPort port, ReadOnlySpan<float> frame)
	{
		var frames = Format.FrameSamples;
		var written = snd_pcm_writei(port.Handle, in MemoryMarshal.GetReference(frame), (ulong)frames);
		if (written < 0)
		{
			snd_pcm_recover(port.Handle, (int)written, 1);
		}
	}

	private void ClosePort(AlsaPort port)
	{
		if (port.Handle == IntPtr.Zero)
		{
			return;
		}

		try
		{
			snd_pcm_drop(port.Handle);
			snd_pcm_close(port.Handle);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_log("engine", $"ALSA close for '{port.DeviceId}' threw: {ex.Message}.");
		}
		finally
		{
			port.Handle = IntPtr.Zero;
		}
	}

	private static string DescribeStream(int streamType) => streamType == SND_PCM_STREAM_CAPTURE ? "capture" : "playback";

	private static string DescribeError(long code)
	{
		try
		{
			var ptr = snd_strerror((int)code);
			return ptr == IntPtr.Zero ? $"error {code}" : Marshal.PtrToStringAnsi(ptr) ?? $"error {code}";
		}
		catch (DllNotFoundException)
		{
			return $"error {code}";
		}
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		Stop();
		_isDisposed = true;
	}

	private sealed class AlsaPort(string deviceId, int streamType)
	{
		public string DeviceId { get; } = deviceId;

		public int StreamType { get; } = streamType;

		public IntPtr Handle { get; set; }
	}

	// ---- libasound P/Invoke (resolved at runtime on Linux only) ----

	[DllImport(Lib, CharSet = CharSet.Ansi)]
	private static extern int snd_pcm_open(out IntPtr pcm, string name, int stream, int mode);

	[DllImport(Lib)]
	private static extern int snd_pcm_set_params(IntPtr pcm, int format, int access, uint channels, uint rate, int softResample, uint latencyUs);

	[DllImport(Lib)]
	private static extern long snd_pcm_readi(IntPtr pcm, ref float buffer, ulong size);

	[DllImport(Lib)]
	private static extern long snd_pcm_writei(IntPtr pcm, in float buffer, ulong size);

	[DllImport(Lib)]
	private static extern int snd_pcm_recover(IntPtr pcm, int err, int silent);

	[DllImport(Lib)]
	private static extern int snd_pcm_drop(IntPtr pcm);

	[DllImport(Lib)]
	private static extern int snd_pcm_close(IntPtr pcm);

	[DllImport(Lib)]
	private static extern IntPtr snd_strerror(int errnum);
}
