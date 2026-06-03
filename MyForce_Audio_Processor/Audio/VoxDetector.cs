// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// PROJECT_FRAMEWORK.md Phase 1: the VOX detect primitive (§3.6.3, §3.6.8). One of the AP's
// two built-in primitives. It turns a radio's per-RX engine level into a debounced Call
// Detect (RX-active) signal using a threshold, an attack (to ignore transients), and a hang
// (so normal speech pauses do not chatter the signal). Implemented once in the AP and reused
// by every radio that declares VOX, including the 4W Resource (§3.6.2).
//
// Pure control-thread logic: deterministic given (level, timestamp), so it is fully testable
// without hardware.

internal sealed class VoxDetector
{
	private readonly double _thresholdLinear;
	private readonly long _attackMs;
	private readonly long _hangMs;

	private bool _isDetected;
	private long _aboveSinceMs = long.MinValue; // when the level first rose above threshold
	private long _lastAboveMs = long.MinValue;   // last time the level was above threshold

	public VoxDetector(double thresholdDb, int attackMs, int hangMs)
	{
		// Threshold is dBFS relative to full-scale (1.0 RMS). Convert to a linear RMS compare.
		_thresholdLinear = DbToLinear(thresholdDb);
		_attackMs = Math.Max(0, attackMs);
		_hangMs = Math.Max(0, hangMs);
	}

	/// <summary>Current debounced Call Detect state.</summary>
	public bool IsDetected => _isDetected;

	/// <summary>
	/// Feed the latest RX RMS level (0..1) with a monotonic millisecond timestamp. Returns the
	/// updated Call Detect state. Call once per control-thread poll of the engine level (§3.6.8).
	/// </summary>
	public bool Update(float rmsLevel, long nowMs)
	{
		var isAbove = rmsLevel >= _thresholdLinear;

		if (isAbove)
		{
			if (_aboveSinceMs == long.MinValue)
			{
				_aboveSinceMs = nowMs;
			}

			_lastAboveMs = nowMs;

			// Assert only after the level has been continuously above threshold for the attack.
			if (!_isDetected && (nowMs - _aboveSinceMs) >= _attackMs)
			{
				_isDetected = true;
			}
		}
		else
		{
			_aboveSinceMs = long.MinValue;

			// Release only after the hang has elapsed since the last above-threshold sample, so
			// brief inter-syllable gaps do not drop the signal.
			if (_isDetected && _lastAboveMs != long.MinValue && (nowMs - _lastAboveMs) >= _hangMs)
			{
				_isDetected = false;
			}
		}

		return _isDetected;
	}

	/// <summary>Force the detector back to idle (e.g. on device loss, §3.6.10).</summary>
	public void Reset()
	{
		_isDetected = false;
		_aboveSinceMs = long.MinValue;
		_lastAboveMs = long.MinValue;
	}

	private static double DbToLinear(double db) => Math.Pow(10.0, db / 20.0);
}
