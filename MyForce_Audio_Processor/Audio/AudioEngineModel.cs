// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// PROJECT_FRAMEWORK.md Phase 1 (compliance plan): Audio Processor execution core.
// This file defines the audio backend abstraction (§3.6.4) and the engine audio
// format (§3.6.5). The real-time engine (§3.6.1/§3.6.6) targets this interface so
// the concrete device API (PipeWire/ALSA/JACK) can be swapped without touching the
// crosspoint matrix.

using System.Collections.ObjectModel;

/// <summary>
/// The fixed internal engine audio format (§3.6.5). A single sample rate and frame
/// size is used across the matrix; device streams are resampled to it at the backend
/// boundary only. Default 48 kHz mono with a ~10 ms frame.
/// </summary>
internal readonly record struct EngineAudioFormat(int SampleRateHz, int FrameSamples, int Channels)
{
	/// <summary>48 kHz, 480-sample (10 ms) mono frame: native to most USB-audio soundcards (§3.6.5).</summary>
	public static EngineAudioFormat Default { get; } = new(48_000, 480, 1);

	/// <summary>16 kHz CPU-saving narrowband alternative for a constrained box (§3.6.5).</summary>
	public static EngineAudioFormat Narrowband { get; } = new(16_000, 160, 1);

	/// <summary>Total samples in one frame across all channels.</summary>
	public int FrameLength => FrameSamples * Channels;

	/// <summary>Nominal wall-clock duration of one frame.</summary>
	public TimeSpan FrameDuration => TimeSpan.FromSeconds((double)FrameSamples / SampleRateHz);
}

/// <summary>Direction of a backend audio port relative to the AP.</summary>
internal enum AudioPortDirection
{
	/// <summary>A capture port: audio flowing into the engine (radio RX, operator mic).</summary>
	Capture,

	/// <summary>A playback port: audio flowing out of the engine (radio TX, speaker).</summary>
	Playback
}

/// <summary>
/// A device the backend can enumerate. Identity is the stable backend-specific id the
/// engine binds to; absence at bind time yields an Unavailable port that returns silence
/// until hotplug returns it (§3.6.10).
/// </summary>
internal sealed record AudioBackendDevice(string Id, string DisplayName, string Role, bool CanCapture, bool CanPlayback);

/// <summary>
/// Hotplug notification raised by a backend when a device appears or disappears (§3.6.10).
/// The engine uses it to mark the affected port Available/Unavailable without changing the
/// matrix slot.
/// </summary>
internal sealed class AudioDeviceHotplugEventArgs : EventArgs
{
	public AudioDeviceHotplugEventArgs(string deviceId, bool isPresent)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
		DeviceId = deviceId;
		IsPresent = isPresent;
	}

	public string DeviceId { get; }

	public bool IsPresent { get; }
}

/// <summary>
/// Result of binding the engine's ordered capture/playback device lists to a backend.
/// Index ordering matches the lists passed to <see cref="IAudioBackend.Bind"/>.
/// </summary>
internal sealed record AudioBackendBinding(
	ReadOnlyCollection<bool> CaptureAvailability,
	ReadOnlyCollection<bool> PlaybackAvailability);

/// <summary>
/// Concrete device I/O behind a fixed interface (§3.6.4). The engine owns routing/mixing
/// and never calls a specific audio API directly; a backend implements capture+playback at
/// the engine format. PipeWire is the default on Linux with ALSA as a deterministic fallback.
///
/// Frame I/O (<see cref="ReadCapture"/>/<see cref="WritePlayback"/>) runs on the RT thread
/// and must be allocation- and lock-free: backends pre-pin/pool their buffers (§3.6.6).
/// </summary>
internal interface IAudioBackend : IDisposable
{
	/// <summary>Human label for logs (e.g. "PipeWire", "ALSA", "null").</summary>
	string Name { get; }

	/// <summary>The engine format this backend has been opened at.</summary>
	EngineAudioFormat Format { get; }

	/// <summary>Enumerate devices and identity for the admin device pickers (§3.6.4).</summary>
	IReadOnlyList<AudioBackendDevice> EnumerateDevices();

	/// <summary>
	/// Bind ordered capture and playback device ids. Called once before <see cref="Start"/>.
	/// A device that is absent still reserves its port (returns silence) so the matrix slot is
	/// stable across unplug/replug (§3.6.10).
	/// </summary>
	AudioBackendBinding Bind(IReadOnlyList<string> captureDeviceIds, IReadOnlyList<string> playbackDeviceIds);

	/// <summary>Open the bound streams and begin the audio clock.</summary>
	void Start();

	/// <summary>Stop the audio clock and close streams.</summary>
	void Stop();

	/// <summary>
	/// Block until the next frame is due, pacing the RT loop. Returns false once the backend
	/// is stopped so the loop can exit. For device-clocked backends (ALSA) this is where the
	/// blocking read happens; for the null backend it is a frame-duration wait.
	/// </summary>
	bool WaitNextFrame();

	/// <summary>Fill <paramref name="frame"/> with the latest samples for a capture port, or silence if unavailable.</summary>
	void ReadCapture(int captureIndex, Span<float> frame);

	/// <summary>Write <paramref name="frame"/> to a playback port; ignored if the port is unavailable.</summary>
	void WritePlayback(int playbackIndex, ReadOnlySpan<float> frame);

	/// <summary>True when the port currently has a live device bound (§3.6.10).</summary>
	bool IsPortAvailable(AudioPortDirection direction, int index);

	/// <summary>Raised when a bound device appears or disappears.</summary>
	event EventHandler<AudioDeviceHotplugEventArgs>? DeviceHotplug;
}
