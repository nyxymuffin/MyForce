// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// PROJECT_FRAMEWORK.md Phase 1: the per-radio TX state machine (§3.4, §3.6.9). It sequences
// the TX-critical path entirely inside the AP control thread so the key-then-wait-then-gate
// ordering never crosses the MQTT broker (§3.4):
//
//   IDLE -> KEY (relay assert OR in-process RM KeyAsync) -> wait ptt_lead_ms / talk-permit
//        -> TX (open mic audio gate) -> un-key (close gate) -> wait ptt_tail_ms -> IDLE
//
// Both keying paths are sequenced identically (§3.4); the difference (relay vs RM) is hidden
// behind injected delegates. Error and edge handling per §3.6.9: key() failure aborts before
// the gate opens, talk-permit timeout un-keys, and an un-key during the lead cancels the
// pending gate-open. Manual one-at-a-time and the double-tap override hook (§3.5) live here,
// between the console PTT request and the per-radio sequence.
//
// The state machine is decoupled from the engine and keying hardware via delegates, so it is
// fully exercisable in isolation.

using MyForce.Contracts.Radio;

/// <summary>Result of a manual PTT request, surfaced to the MQTT ack (§5.8.2).</summary>
internal sealed record TxOutcome(bool Accepted, string Detail)
{
	public static TxOutcome Ok(string detail) => new(true, detail);

	public static TxOutcome Rejected(string detail) => new(false, detail);
}

/// <summary>
/// Drives keying and the mic audio gate for every declared radio, enforcing the §3.4 ordering
/// and the §3.5 arbitration. Replaces the earlier synchronous TxController while preserving the
/// <see cref="RadioTxState"/> query surface the MQTT payloads consume.
/// </summary>
internal sealed class TxStateMachine : IAsyncDisposable
{
	// Default ceiling for waiting on an RM-reported talk-permit before giving up (§3.6.9).
	private const int DefaultTalkPermitTimeoutMs = 3_000;

	private readonly Dictionary<string, TxRadioContext> _contexts;
	private readonly Func<RadioId, CancellationToken, Task<bool>> _keyAsync;
	private readonly Func<RadioId, CancellationToken, Task> _unkeyAsync;
	private readonly Func<RadioId, bool?> _tryTalkPermitReady;
	private readonly Action<RadioId, bool> _setMicGate;
	private readonly Func<RadioId, Task> _publishStateAsync;
	private readonly Action<string, string> _log;
	private readonly SemaphoreSlim _arbitration = new(1, 1);

	public TxStateMachine(
		IEnumerable<RadioRuntimeDefinition> radios,
		Func<RadioId, CancellationToken, Task<bool>> keyAsync,
		Func<RadioId, CancellationToken, Task> unkeyAsync,
		Func<RadioId, bool?> tryTalkPermitReady,
		Action<RadioId, bool> setMicGate,
		Func<RadioId, Task> publishStateAsync,
		Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(radios);
		ArgumentNullException.ThrowIfNull(keyAsync);
		ArgumentNullException.ThrowIfNull(unkeyAsync);
		ArgumentNullException.ThrowIfNull(tryTalkPermitReady);
		ArgumentNullException.ThrowIfNull(setMicGate);
		ArgumentNullException.ThrowIfNull(publishStateAsync);
		ArgumentNullException.ThrowIfNull(log);

		_keyAsync = keyAsync;
		_unkeyAsync = unkeyAsync;
		_tryTalkPermitReady = tryTalkPermitReady;
		_setMicGate = setMicGate;
		_publishStateAsync = publishStateAsync;
		_log = log;
		_contexts = radios.ToDictionary(
			static radio => radio.Id.Value,
			static radio => new TxRadioContext(radio),
			StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>The single radio that currently holds manual TX system-wide, or null (§5.4).</summary>
	public RadioId? ActiveManualTransmitRadioId { get; private set; }

	/// <summary>Current TX state for a known radio, for MQTT publishing.</summary>
	public RadioTxState GetState(RadioId radioId)
	{
		ArgumentNullException.ThrowIfNull(radioId);
		return GetContext(radioId).State;
	}

	/// <summary>
	/// Begin a manual transmit (PTT down). Enforces one-at-a-time with the double-tap override
	/// hook (§3.5), then runs the §3.4 KEY -> wait -> GATE sequence. Returns once the gate is
	/// open (or the request was rejected/aborted).
	/// </summary>
	public async Task<TxOutcome> RequestKeyAsync(RadioId radioId, bool isOverride, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(radioId);

		await _arbitration.WaitAsync(cancellationToken).ConfigureAwait(false);
		TxRadioContext context;
		try
		{
			context = GetContext(radioId);

			// One manual TX at a time across the system (§3.5, §5.4). A double-tap override is
			// reserved for seizing a radio a bridge is actively repeating onto (§3.5); the bridge
			// engine is Phase 2, so today override only annotates the contention decision.
			if (ActiveManualTransmitRadioId is not null && ActiveManualTransmitRadioId != radioId)
			{
				if (!isOverride)
				{
					return TxOutcome.Rejected($"Radio '{ActiveManualTransmitRadioId.Value}' already holds manual transmit.");
				}

				_log("tx", $"Override requested for '{radioId.Value}' while '{ActiveManualTransmitRadioId.Value}' holds TX; seizing.");
			}

			if (context.State.State is not TxStatePhase.Idle)
			{
				return TxOutcome.Rejected($"Radio '{radioId.Value}' is already in {context.State.State} state.");
			}

			ActiveManualTransmitRadioId = radioId;
			context.BeginSequence();
		}
		finally
		{
			_arbitration.Release();
		}

		return await RunKeySequenceAsync(context, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// End a manual transmit (PTT up). Closes the gate, runs the tail, and un-keys. If the
	/// release lands during the lead (before the gate opened), the pending gate-open is
	/// cancelled and the gate never opens (§3.6.9).
	/// </summary>
	public async Task<TxOutcome> RequestUnkeyAsync(RadioId radioId, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(radioId);

		TxRadioContext context;
		await _arbitration.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			context = GetContext(radioId);
			if (ActiveManualTransmitRadioId != radioId)
			{
				return TxOutcome.Rejected("Radio is not the active manual transmit target.");
			}

			// Cancel any in-flight lead wait so an early release does not open the gate (§3.6.9).
			context.CancelSequence();
		}
		finally
		{
			_arbitration.Release();
		}

		await context.SequenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var phase = context.State.State;
			if (phase is TxStatePhase.Transmitting)
			{
				// Normal release from full TX: close gate -> tail -> un-key.
				_setMicGate(radioId, false);
				await TransitionAsync(context, TxStatePhase.Tail, isKeyAsserted: true).ConfigureAwait(false);
				await DelayQuietAsync(context.Radio.Config.Keying.PttTailMs, cancellationToken).ConfigureAwait(false);
			}

			await _unkeyAsync(radioId, cancellationToken).ConfigureAwait(false);
			await TransitionAsync(context, TxStatePhase.Idle, isKeyAsserted: false).ConfigureAwait(false);
			ClearActiveIfMatches(radioId);
			return TxOutcome.Ok($"Released after {context.Radio.Config.Keying.PttTailMs} ms tail.");
		}
		finally
		{
			context.SequenceGate.Release();
		}
	}

	private async Task<TxOutcome> RunKeySequenceAsync(TxRadioContext context, CancellationToken cancellationToken)
	{
		var radioId = context.Radio.Id;
		var keying = context.Radio.Config.Keying;
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.SequenceCancellation);
		var token = linked.Token;

		await context.SequenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// KEY: assert the relay or call the RM in-process (§3.6.3). Failure aborts (§3.6.9).
			await TransitionAsync(context, TxStatePhase.Keying, isKeyAsserted: false).ConfigureAwait(false);
			bool keyed;
			try
			{
				keyed = await _keyAsync(radioId, token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return await AbortBeforeGateAsync(context, "Key cancelled before the gate opened.").ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				_log("tx", $"Key for '{radioId.Value}' threw: {ex.Message}.");
				return await AbortBeforeGateAsync(context, $"Key failed: {ex.Message}").ConfigureAwait(false);
			}

			if (!keyed)
			{
				return await AbortBeforeGateAsync(context, "Key assertion failed; gate not opened.").ConfigureAwait(false);
			}

			context.MarkKeyAsserted();

			// WAIT: a talk-permit (trunked/digital) or a fixed lead (§3.4).
			try
			{
				if (keying.TalkPermit)
				{
					if (!await WaitForTalkPermitAsync(radioId, token).ConfigureAwait(false))
					{
						return await AbortBeforeGateAsync(context, "Talk-permit timeout; un-keyed.").ConfigureAwait(false);
					}
				}
				else
				{
					await Task.Delay(Math.Max(0, keying.PttLeadMs), token).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException)
			{
				// Un-key during the lead: cancel the pending gate-open (§3.6.9).
				return await AbortBeforeGateAsync(context, "Released during lead; gate never opened.").ConfigureAwait(false);
			}

			// TX: open the mic audio gate.
			_setMicGate(radioId, true);
			await TransitionAsync(context, TxStatePhase.Transmitting, isKeyAsserted: true, talkPermitReady: true).ConfigureAwait(false);
			return TxOutcome.Ok($"Transmitting after {(keying.TalkPermit ? "talk-permit" : keying.PttLeadMs + " ms lead")}.");
		}
		finally
		{
			context.SequenceGate.Release();
		}
	}

	private async Task<TxOutcome> AbortBeforeGateAsync(TxRadioContext context, string detail)
	{
		var radioId = context.Radio.Id;
		try
		{
			await _unkeyAsync(radioId, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_log("tx", $"Un-key during abort for '{radioId.Value}' threw: {ex.Message}.");
		}

		_setMicGate(radioId, false);
		await TransitionAsync(context, TxStatePhase.Idle, isKeyAsserted: false).ConfigureAwait(false);
		ClearActiveIfMatches(radioId);
		_log("tx", $"TX aborted for '{radioId.Value}': {detail}");
		return TxOutcome.Rejected(detail);
	}

	private async Task<bool> WaitForTalkPermitAsync(RadioId radioId, CancellationToken token)
	{
		var deadline = DateTimeOffset.UtcNow.AddMilliseconds(DefaultTalkPermitTimeoutMs);
		while (DateTimeOffset.UtcNow < deadline)
		{
			token.ThrowIfCancellationRequested();
			if (_tryTalkPermitReady(radioId) == true)
			{
				return true;
			}

			await Task.Delay(10, token).ConfigureAwait(false);
		}

		return false;
	}

	private static async Task DelayQuietAsync(int milliseconds, CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(Math.Max(0, milliseconds), cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// A cancelled tail still completes the un-key path; swallow so release always finishes.
		}
	}

	private async Task TransitionAsync(TxRadioContext context, TxStatePhase phase, bool isKeyAsserted, bool talkPermitReady = false)
	{
		context.SetState(context.State with
		{
			State = phase,
			IsKeyAsserted = isKeyAsserted,
			IsTalkPermitReady = talkPermitReady,
			LastTransitionUtc = DateTimeOffset.UtcNow
		});

		await _publishStateAsync(context.Radio.Id).ConfigureAwait(false);
	}

	private void ClearActiveIfMatches(RadioId radioId)
	{
		if (ActiveManualTransmitRadioId == radioId)
		{
			ActiveManualTransmitRadioId = null;
		}
	}

	private TxRadioContext GetContext(RadioId radioId)
	{
		if (_contexts.TryGetValue(radioId.Value, out var context))
		{
			return context;
		}

		throw new InvalidOperationException($"Unknown radio id '{radioId.Value}'.");
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var context in _contexts.Values)
		{
			context.Dispose();
		}

		_arbitration.Dispose();
		await ValueTask.CompletedTask;
	}

	/// <summary>Per-radio runtime context: current state, the in-flight sequence cancellation, and a serializing gate.</summary>
	private sealed class TxRadioContext : IDisposable
	{
		private CancellationTokenSource _sequenceCts = new();

		public TxRadioContext(RadioRuntimeDefinition radio)
		{
			Radio = radio;
			State = RadioTxState.Create(radio);
		}

		public RadioRuntimeDefinition Radio { get; }

		public RadioTxState State { get; private set; }

		public SemaphoreSlim SequenceGate { get; } = new(1, 1);

		public CancellationToken SequenceCancellation => _sequenceCts.Token;

		public void SetState(RadioTxState state) => State = state;

		public void MarkKeyAsserted() => State = State with { IsKeyAsserted = true };

		public void BeginSequence()
		{
			if (_sequenceCts.IsCancellationRequested)
			{
				_sequenceCts.Dispose();
				_sequenceCts = new CancellationTokenSource();
			}
		}

		public void CancelSequence() => _sequenceCts.Cancel();

		public void Dispose()
		{
			_sequenceCts.Dispose();
			SequenceGate.Dispose();
		}
	}
}
