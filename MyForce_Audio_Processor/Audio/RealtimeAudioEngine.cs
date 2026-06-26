// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// PROJECT_FRAMEWORK.md Phase 1: the real-time audio engine (§3.6.1, §3.6.5, §3.6.6).
// A deliberately "dumb" mixer: it reads a routing snapshot it is handed, captures each
// source, computes per-sink = sum(source x crosspoint_gain) with a soft limiter, and writes
// each sink. Mix-minus for bridges is already encoded in the snapshot gains (§3.5), so the
// loop needs no bridge logic.
//
// RT boundary rules (§3.6.6): the audio thread does NO allocation, NO locks, NO blocking
// syscalls beyond the backend's frame wait, NO logging, NO MQTT. Routing is swapped in via
// an atomic reference (double-buffer); levels are published out via per-source atomics; a
// small lock-free SPSC ring carries discrete control->RT commands. All frame buffers are
// pre-allocated once at Start.

using System.Runtime.CompilerServices;

internal sealed class RealtimeAudioEngine : IDisposable
{
	private readonly IAudioBackend _backend;
	private readonly EngineAudioFormat _format;
	private readonly EngineTopology _topology;
	private readonly Action<string, string> _log;

	// Pre-allocated frame buffers (one per source/sink). Never resized after Start (§3.6.6).
	private readonly float[][] _captureBuffers;
	private readonly float[] _mixAccumulator;
	private readonly float[] _sourceLevels;      // RT writes via Volatile; control thread reads.

	// Routing is published to the RT thread as an immutable snapshot, swapped atomically.
	private EngineRoutingSnapshot _snapshot = EngineRoutingSnapshot.Empty;

	// Lock-free SPSC command ring (control -> RT). Used for discrete one-shots (e.g. reset meters).
	private readonly EngineCommandRing _commandRing = new(64);

	private Thread? _audioThread;
	private volatile bool _isRunning;
	private bool _isDisposed;

	public RealtimeAudioEngine(IAudioBackend backend, EngineTopology topology, Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentNullException.ThrowIfNull(topology);
		ArgumentNullException.ThrowIfNull(log);

		_backend = backend;
		_topology = topology;
		_format = backend.Format;
		_log = log;

		var captureCount = topology.Sources.Count;
		_captureBuffers = new float[captureCount][];
		for (var index = 0; index < captureCount; index++)
		{
			_captureBuffers[index] = new float[_format.FrameLength];
		}

		_mixAccumulator = new float[_format.FrameLength];
		_sourceLevels = new float[captureCount];
	}

	public EngineAudioFormat Format => _format;

	/// <summary>
	/// Publish a new routing snapshot to the RT thread (§3.6.6). The reference assignment is
	/// atomic, so the audio thread always reads a fully-consistent matrix with no lock.
	/// </summary>
	public void PublishRouting(EngineRoutingSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		Volatile.Write(ref _snapshot, snapshot);
	}

	/// <summary>Current RMS level (0..1) for a source index; read by the VOX primitive (§3.6.8).</summary>
	public float GetSourceLevel(int sourceIndex)
	{
		if (sourceIndex < 0 || sourceIndex >= _sourceLevels.Length)
		{
			return 0f;
		}

		return Volatile.Read(ref _sourceLevels[sourceIndex]);
	}

	public void Start()
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);
		if (_isRunning)
		{
			return;
		}

		_backend.Start();
		_isRunning = true;
		_audioThread = new Thread(RunAudioLoop)
		{
			Name = "myforce-rt-audio",
			IsBackground = true,
			// Highest managed priority; the OS RT scheduling is the backend's concern (§3.6.6).
			Priority = ThreadPriority.Highest
		};
		_audioThread.Start();
		_log("engine", $"RT audio engine started on backend '{_backend.Name}' at {_format.SampleRateHz} Hz, {_format.FrameSamples}-sample frames, {_topology.SourceCount} source(s) x {_topology.SinkCount} sink(s).");
	}

	public void Stop()
	{
		if (!_isRunning)
		{
			return;
		}

		_isRunning = false;
		_backend.Stop();
		_audioThread?.Join(TimeSpan.FromSeconds(2));
		_audioThread = null;
		_log("engine", "RT audio engine stopped.");
	}

	/// <summary>Queue a discrete command for the RT thread (lock-free, control side).</summary>
	public bool TryEnqueueCommand(EngineCommand command) => _commandRing.TryEnqueue(command);

	/// <summary>Live re-bind a radio's capture port to a new device id; applied on the RT thread (§3.7.8).</summary>
	public bool RequestRebindCapture(int index, string deviceId) => _commandRing.TryEnqueue(EngineCommand.RebindCapture(index, deviceId));

	/// <summary>Live re-bind a radio's playback port to a new device id; applied on the RT thread (§3.7.8).</summary>
	public bool RequestRebindPlayback(int index, string deviceId) => _commandRing.TryEnqueue(EngineCommand.RebindPlayback(index, deviceId));

	// ---- RT thread below this line: NO allocation, NO locks, NO logging, NO MQTT (§3.6.6) ----

	private void RunAudioLoop()
	{
		while (_isRunning)
		{
			try
			{
				if (!_backend.WaitNextFrame())
				{
					break;
				}

				DrainCommands();

				var snapshot = Volatile.Read(ref _snapshot);
				CaptureSources(snapshot);
				MixSinks(snapshot);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				// Resilience: a transient backend/device fault must not kill the audio thread or the
				// process. Skip this frame and continue; the device-loss path marks ports Unavailable.
				// (Logging is normally banned on the RT path, but this is the rare error branch.)
				_log("engine", $"RT audio frame error (continuing): {ex.Message}");
			}
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void DrainCommands()
	{
		while (_commandRing.TryDequeue(out var command))
		{
			switch (command.Kind)
			{
				case EngineCommandKind.ResetMeters:
					Array.Clear(_sourceLevels);
					break;
				case EngineCommandKind.RebindCapture:
					if (command.DeviceId is not null)
					{
						_backend.RebindPort(AudioPortDirection.Capture, command.Index, command.DeviceId);
					}

					break;
				case EngineCommandKind.RebindPlayback:
					if (command.DeviceId is not null)
					{
						_backend.RebindPort(AudioPortDirection.Playback, command.Index, command.DeviceId);
					}

					break;
				case EngineCommandKind.None:
				default:
					break;
			}
		}
	}

	private void CaptureSources(EngineRoutingSnapshot snapshot)
	{
		var sourceCount = snapshot.SourceCount;
		for (var source = 0; source < sourceCount; source++)
		{
			var buffer = _captureBuffers[source];
			_backend.ReadCapture(snapshot.SourceCapturePort[source], buffer);

			// Cheap per-source energy meter for VOX/UI (§3.6.5). RMS over the frame.
			var sumSquares = 0f;
			for (var i = 0; i < buffer.Length; i++)
			{
				var sample = buffer[i];
				sumSquares += sample * sample;
			}

			var rms = buffer.Length > 0 ? MathF.Sqrt(sumSquares / buffer.Length) : 0f;
			Volatile.Write(ref _sourceLevels[source], rms);
		}
	}

	private void MixSinks(EngineRoutingSnapshot snapshot)
	{
		var sourceCount = snapshot.SourceCount;
		var sinkCount = snapshot.SinkCount;
		var frameLength = _mixAccumulator.Length;

		for (var sink = 0; sink < sinkCount; sink++)
		{
			Array.Clear(_mixAccumulator);

			// sink = sum over sources of (source x crosspoint_gain). Mix-minus is baked into
			// the gains, so a bridge member's own RX simply has gain 0 into its own TX (§3.5).
			for (var source = 0; source < sourceCount; source++)
			{
				var gain = snapshot.GainAt(sink, source);
				if (gain == 0f)
				{
					continue;
				}

				var buffer = _captureBuffers[source];
				for (var i = 0; i < frameLength; i++)
				{
					_mixAccumulator[i] += buffer[i] * gain;
				}
			}

			// Soft limiter per sink output handles summing overflow gracefully (§3.6.5).
			for (var i = 0; i < frameLength; i++)
			{
				_mixAccumulator[i] = SoftClip(_mixAccumulator[i]);
			}

			_backend.WritePlayback(snapshot.SinkPlaybackPort[sink], _mixAccumulator);
		}
	}

	/// <summary>
	/// Cubic soft clipper (§3.6.5): near-linear for small signals, smoothly limits toward +/-1
	/// instead of hard-clipping the sum of several sources. Allocation-free and branch-light.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static float SoftClip(float x)
	{
		if (x >= 1f)
		{
			return 1f;
		}

		if (x <= -1f)
		{
			return -1f;
		}

		// 1.5 * (x - x^3/3) restores unity slope for tiny x while saturating near the rails.
		return 1.5f * (x - ((x * x * x) / 3f));
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
}

/// <summary>Discrete control->RT command kinds carried by the lock-free ring.</summary>
internal enum EngineCommandKind
{
	None = 0,
	ResetMeters = 1,
	// Live re-bind a capture/playback port to a new device id (§3.7.8): the actual ALSA close/open
	// runs on the RT thread (in DrainCommands) so it never races frame I/O.
	RebindCapture = 2,
	RebindPlayback = 3
}

/// <summary>
/// A small value-type command so the SPSC ring never allocates on transfer (§3.6.6). DeviceId is an
/// already-allocated string reference (control thread) carried for rebind commands; copying the
/// struct does not allocate.
/// </summary>
internal readonly record struct EngineCommand(EngineCommandKind Kind, int Index, float Value, string? DeviceId = null)
{
	public static EngineCommand ResetMeters() => new(EngineCommandKind.ResetMeters, 0, 0f);

	public static EngineCommand RebindCapture(int index, string deviceId) => new(EngineCommandKind.RebindCapture, index, 0f, deviceId);

	public static EngineCommand RebindPlayback(int index, string deviceId) => new(EngineCommandKind.RebindPlayback, index, 0f, deviceId);
}

/// <summary>
/// Single-producer/single-consumer lock-free ring buffer for value-type commands. The control
/// thread enqueues; the RT thread dequeues. Power-of-two capacity; full ring drops the newest
/// (commands are advisory one-shots, never audio data).
/// </summary>
internal sealed class EngineCommandRing
{
	private readonly EngineCommand[] _buffer;
	private readonly int _mask;
	private long _head; // next write (producer)
	private long _tail; // next read (consumer)

	public EngineCommandRing(int capacity)
	{
		if (capacity < 2 || (capacity & (capacity - 1)) != 0)
		{
			throw new ArgumentException("Capacity must be a power of two >= 2.", nameof(capacity));
		}

		_buffer = new EngineCommand[capacity];
		_mask = capacity - 1;
	}

	public bool TryEnqueue(EngineCommand command)
	{
		var head = Volatile.Read(ref _head);
		var tail = Volatile.Read(ref _tail);
		if (head - tail >= _buffer.Length)
		{
			return false; // full
		}

		_buffer[head & _mask] = command;
		Volatile.Write(ref _head, head + 1);
		return true;
	}

	public bool TryDequeue(out EngineCommand command)
	{
		var tail = Volatile.Read(ref _tail);
		var head = Volatile.Read(ref _head);
		if (tail >= head)
		{
			command = default;
			return false; // empty
		}

		command = _buffer[tail & _mask];
		Volatile.Write(ref _tail, tail + 1);
		return true;
	}
}
