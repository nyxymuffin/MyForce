// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// PROJECT_FRAMEWORK.md Phase 1: a portable backend implementing IAudioBackend (§3.6.4)
// with no real device I/O. It paces the RT loop at the engine frame rate, returns silence
// on capture, and discards playback. This lets the engine, matrix, TX state machine, and
// VOX run and be exercised on any OS (including the Windows dev box) while the concrete
// PipeWire/ALSA backends are used on the in-vehicle Linux target.

using System.Diagnostics;

internal sealed class NullAudioBackend : IAudioBackend
{
	private readonly object _gate = new();
	private readonly Stopwatch _clock = Stopwatch.StartNew();

	private bool[] _captureAvailable = Array.Empty<bool>();
	private bool[] _playbackAvailable = Array.Empty<bool>();
	private long _frameIndex;
	private bool _isRunning;
	private bool _isDisposed;

	public NullAudioBackend(EngineAudioFormat format)
	{
		Format = format;
	}

	public string Name => "null";

	public EngineAudioFormat Format { get; }

	public event EventHandler<AudioDeviceHotplugEventArgs>? DeviceHotplug;

	public IReadOnlyList<AudioBackendDevice> EnumerateDevices() => Array.Empty<AudioBackendDevice>();

	public AudioBackendBinding Bind(IReadOnlyList<string> captureDeviceIds, IReadOnlyList<string> playbackDeviceIds)
	{
		ArgumentNullException.ThrowIfNull(captureDeviceIds);
		ArgumentNullException.ThrowIfNull(playbackDeviceIds);

		// The null backend has no hardware, so every bound port is "present" (it simply
		// produces silence). This keeps the engine's slot model exercised end to end.
		_captureAvailable = Enumerable.Repeat(true, captureDeviceIds.Count).ToArray();
		_playbackAvailable = Enumerable.Repeat(true, playbackDeviceIds.Count).ToArray();

		return new AudioBackendBinding(
			Array.AsReadOnly((bool[])_captureAvailable.Clone()),
			Array.AsReadOnly((bool[])_playbackAvailable.Clone()));
	}

	public void Start()
	{
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);
			_clock.Restart();
			_frameIndex = 0;
			_isRunning = true;
		}
	}

	public void Stop()
	{
		lock (_gate)
		{
			_isRunning = false;
		}
	}

	public bool WaitNextFrame()
	{
		if (!Volatile.Read(ref _isRunning))
		{
			return false;
		}

		// Pace the loop so one frame elapses per call without busy-spinning the CPU.
		var nextFrameTicks = (++_frameIndex) * Format.FrameDuration.Ticks;
		var dueAt = TimeSpan.FromTicks(nextFrameTicks);
		var remaining = dueAt - _clock.Elapsed;
		if (remaining > TimeSpan.Zero)
		{
			// Sleep most of the interval; a short spin would be wasteful for a silent backend.
			Thread.Sleep(remaining);
		}

		return Volatile.Read(ref _isRunning);
	}

	public void ReadCapture(int captureIndex, Span<float> frame)
	{
		// No hardware: deliver silence. The engine still meters and routes it normally.
		frame.Clear();
	}

	public void WritePlayback(int playbackIndex, ReadOnlySpan<float> frame)
	{
		// No hardware: discard. Intentionally a no-op.
	}

	public bool IsPortAvailable(AudioPortDirection direction, int index)
	{
		var map = direction == AudioPortDirection.Capture ? _captureAvailable : _playbackAvailable;
		return index >= 0 && index < map.Length && map[index];
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_isDisposed = true;
			_isRunning = false;
		}
	}

	/// <summary>Test/diagnostic hook to simulate a device disappearing or returning (§3.6.10).</summary>
	public void SimulateHotplug(AudioPortDirection direction, int index, bool isPresent)
	{
		var map = direction == AudioPortDirection.Capture ? _captureAvailable : _playbackAvailable;
		if (index < 0 || index >= map.Length)
		{
			return;
		}

		map[index] = isPresent;
		DeviceHotplug?.Invoke(this, new AudioDeviceHotplugEventArgs($"{direction}:{index}", isPresent));
	}
}
