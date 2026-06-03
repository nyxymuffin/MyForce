// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// PROJECT_FRAMEWORK.md Phase 1: the crosspoint routing model (§3.5, §3.6.5).
// Sources (radio RX, operator mic, future media) x sinks (radio TX, speaker) with a
// per-crosspoint gain. Mix-minus for conference bridges (§3.5) is expressed purely as
// gain assignment: a bridge member's TX sink sums every OTHER member's RX, never its own,
// so the engine needs no special-case logic, only "sink = sum(source x gain)".
//
// The snapshot is immutable and swapped to the RT thread atomically (§3.6.6); the builder
// runs on the control thread.

using System.Collections.ObjectModel;

/// <summary>Kind of a logical engine endpoint, for readability and admin/state reporting.</summary>
internal enum EngineEndpointKind
{
	RadioRx,
	RadioTx,
	OperatorMic,
	Speaker,
	Media
}

/// <summary>
/// A fixed logical endpoint in the matrix, bound to a backend port. The port index is fixed
/// for the engine's lifetime so a slot is stable across device unplug/replug (§3.6.10).
/// </summary>
internal sealed record EngineEndpoint(string Id, EngineEndpointKind Kind, int PortIndex);

/// <summary>
/// The fixed set of sources and sinks built at boot from the declared topology (§3.6.10).
/// Indices into <see cref="Sources"/>/<see cref="Sinks"/> are the matrix coordinates.
/// </summary>
internal sealed class EngineTopology
{
	private readonly Dictionary<string, int> _sourceIndexById;
	private readonly Dictionary<string, int> _sinkIndexById;

	public EngineTopology(IReadOnlyList<EngineEndpoint> sources, IReadOnlyList<EngineEndpoint> sinks)
	{
		ArgumentNullException.ThrowIfNull(sources);
		ArgumentNullException.ThrowIfNull(sinks);

		Sources = new ReadOnlyCollection<EngineEndpoint>(sources.ToArray());
		Sinks = new ReadOnlyCollection<EngineEndpoint>(sinks.ToArray());
		_sourceIndexById = BuildIndex(Sources);
		_sinkIndexById = BuildIndex(Sinks);
	}

	public ReadOnlyCollection<EngineEndpoint> Sources { get; }

	public ReadOnlyCollection<EngineEndpoint> Sinks { get; }

	public int SourceCount => Sources.Count;

	public int SinkCount => Sinks.Count;

	public bool TryGetSourceIndex(string id, out int index) => _sourceIndexById.TryGetValue(id, out index);

	public bool TryGetSinkIndex(string id, out int index) => _sinkIndexById.TryGetValue(id, out index);

	private static Dictionary<string, int> BuildIndex(IReadOnlyList<EngineEndpoint> endpoints)
	{
		var map = new Dictionary<string, int>(endpoints.Count, StringComparer.OrdinalIgnoreCase);
		for (var index = 0; index < endpoints.Count; index++)
		{
			map[endpoints[index].Id] = index;
		}

		return map;
	}
}

/// <summary>
/// Immutable routing state read by the RT thread (§3.6.6). Holds the dense gain matrix and
/// the source/sink to backend-port maps. Never mutated after construction.
/// </summary>
internal sealed class EngineRoutingSnapshot
{
	private readonly float[] _gains; // row-major: [sink * SourceCount + source]

	internal EngineRoutingSnapshot(int sourceCount, int sinkCount, float[] gains, int[] sourceCapturePort, int[] sinkPlaybackPort)
	{
		SourceCount = sourceCount;
		SinkCount = sinkCount;
		_gains = gains;
		SourceCapturePort = sourceCapturePort;
		SinkPlaybackPort = sinkPlaybackPort;
	}

	public int SourceCount { get; }

	public int SinkCount { get; }

	/// <summary>Backend capture port feeding each source index.</summary>
	public int[] SourceCapturePort { get; }

	/// <summary>Backend playback port drained by each sink index.</summary>
	public int[] SinkPlaybackPort { get; }

	/// <summary>Linear gain at a crosspoint; 0 means the crosspoint is not routed.</summary>
	public float GainAt(int sink, int source) => _gains[(sink * SourceCount) + source];

	public static EngineRoutingSnapshot Empty { get; } = new(0, 0, Array.Empty<float>(), Array.Empty<int>(), Array.Empty<int>());
}

/// <summary>
/// Builds <see cref="EngineRoutingSnapshot"/> instances on the control thread. Start from the
/// fixed topology, set crosspoint gains (including mix-minus bridge fan-out), then call
/// <see cref="Build"/> and hand the result to the engine for an atomic swap.
/// </summary>
internal sealed class EngineRoutingBuilder
{
	private readonly EngineTopology _topology;
	private readonly float[] _gains;
	private readonly int[] _sourceCapturePort;
	private readonly int[] _sinkPlaybackPort;

	public EngineRoutingBuilder(EngineTopology topology)
	{
		ArgumentNullException.ThrowIfNull(topology);
		_topology = topology;
		_gains = new float[topology.SinkCount * topology.SourceCount];
		_sourceCapturePort = topology.Sources.Select(static source => source.PortIndex).ToArray();
		_sinkPlaybackPort = topology.Sinks.Select(static sink => sink.PortIndex).ToArray();
	}

	/// <summary>Set a single crosspoint gain by endpoint id. Unknown ids are ignored.</summary>
	public EngineRoutingBuilder SetGain(string sourceId, string sinkId, float gain)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
		ArgumentException.ThrowIfNullOrWhiteSpace(sinkId);

		if (_topology.TryGetSourceIndex(sourceId, out var source) && _topology.TryGetSinkIndex(sinkId, out var sink))
		{
			_gains[(sink * _topology.SourceCount) + source] = gain;
		}

		return this;
	}

	/// <summary>Clear any routing into a sink (e.g. when a TX gate closes or a device is lost).</summary>
	public EngineRoutingBuilder ClearSink(string sinkId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sinkId);

		if (_topology.TryGetSinkIndex(sinkId, out var sink))
		{
			Array.Clear(_gains, sink * _topology.SourceCount, _topology.SourceCount);
		}

		return this;
	}

	public EngineRoutingSnapshot Build()
	{
		return new EngineRoutingSnapshot(
			_topology.SourceCount,
			_topology.SinkCount,
			(float[])_gains.Clone(),
			(int[])_sourceCapturePort.Clone(),
			(int[])_sinkPlaybackPort.Clone());
	}
}
