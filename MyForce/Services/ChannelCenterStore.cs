using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyForce.Services;

/// <summary>
/// A geographic "center" (Lat/Long) configured for one radio channel. Centers are
/// optional, not every channel needs one, and they are set via the radio GEO AREA
/// screen. They feed the patrol PROXIMITY LIST (closest centers to the vehicle).
/// </summary>
public sealed record ChannelCenter(string RadioId, string Channel, double Latitude, double Longitude);

/// <summary>
/// Persists per-channel geographic centers to a UI config file so they survive reboots,
/// keyed by (radio id, channel). The radio channel list is radio-type/plugin specific
/// and not available yet, so this store is the groundwork: the GEO AREA UI writes centers
/// here, and the proximity ranking reads them.
/// </summary>
internal sealed class ChannelCenterStore
{
	private const string FileName = "myforce-channel-centers.json";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	private readonly object _gate = new();
	private List<ChannelCenter>? _cache;

	/// <summary>All configured centers across every radio.</summary>
	public IReadOnlyList<ChannelCenter> GetAll()
	{
		lock (_gate)
		{
			return Load().ToArray();
		}
	}

	/// <summary>The centers configured for a single radio.</summary>
	public IReadOnlyList<ChannelCenter> GetForRadio(string radioId)
	{
		lock (_gate)
		{
			return Load().Where(center => Eq(center.RadioId, radioId)).ToArray();
		}
	}

	/// <summary>The center for one channel, or null if none is set (centers are optional).</summary>
	public ChannelCenter? Get(string radioId, string channel)
	{
		lock (_gate)
		{
			return Load().FirstOrDefault(center => Eq(center.RadioId, radioId) && Eq(center.Channel, channel));
		}
	}

	/// <summary>Sets (or replaces) the center for one channel and persists.</summary>
	public void Set(string radioId, string channel, double latitude, double longitude)
	{
		lock (_gate)
		{
			var list = Load();
			list.RemoveAll(center => Eq(center.RadioId, radioId) && Eq(center.Channel, channel));
			list.Add(new ChannelCenter(radioId, channel, latitude, longitude));
			Save(list);
		}
	}

	/// <summary>Removes the center for one channel (back to "no center") and persists.</summary>
	public void Clear(string radioId, string channel)
	{
		lock (_gate)
		{
			var list = Load();
			if (list.RemoveAll(center => Eq(center.RadioId, radioId) && Eq(center.Channel, channel)) > 0)
			{
				Save(list);
			}
		}
	}

	private List<ChannelCenter> Load()
	{
		if (_cache is not null)
		{
			return _cache;
		}

		string path = UiConfigPaths.Resolve(FileName);
		if (!File.Exists(path))
		{
			_cache = new List<ChannelCenter>();
			return _cache;
		}

		// A missing/locked/corrupt file must not crash the UI; treat it as "no centers".
		try
		{
			string json = File.ReadAllText(path);
			_cache = JsonSerializer.Deserialize<List<ChannelCenter>>(json, JsonOptions) ?? new List<ChannelCenter>();
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
			_cache = new List<ChannelCenter>();
		}

		return _cache;
	}

	private void Save(List<ChannelCenter> list)
	{
		_cache = list;
		string path = UiConfigPaths.Resolve(FileName);
		string? directory = Path.GetDirectoryName(path);

		try
		{
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOptions));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best-effort persistence; an unwritable config dir must not crash the UI.
		}
	}

	private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
