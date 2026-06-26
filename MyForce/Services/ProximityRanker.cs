using System;
using System.Collections.Generic;
using System.Linq;

namespace MyForce.Services;

/// <summary>A channel center paired with its great-circle distance from a reference point.</summary>
internal sealed record RankedChannelCenter(ChannelCenter Center, double DistanceKm);

/// <summary>
/// Ranks configured channel centers by distance from the vehicle's current location so the
/// patrol PROXIMITY LIST can show the nearest channels. Pure/Linux-safe math (no UI deps).
/// </summary>
internal static class ProximityRanker
{
	private const double EarthRadiusKm = 6371.0088;

	/// <summary>Great-circle distance (km) between two lat/long points (haversine).</summary>
	public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
	{
		double dLat = ToRadians(lat2 - lat1);
		double dLon = ToRadians(lon2 - lon1);
		double a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
			+ (Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
		return EarthRadiusKm * 2 * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
	}

	/// <summary>
	/// The <paramref name="count"/> centers closest to (<paramref name="latitude"/>,
	/// <paramref name="longitude"/>), nearest first.
	/// </summary>
	public static IReadOnlyList<RankedChannelCenter> Nearest(double latitude, double longitude, IEnumerable<ChannelCenter> centers, int count)
	{
		ArgumentNullException.ThrowIfNull(centers);
		return centers
			.Select(center => new RankedChannelCenter(center, HaversineKm(latitude, longitude, center.Latitude, center.Longitude)))
			.OrderBy(ranked => ranked.DistanceKm)
			.Take(count)
			.ToArray();
	}

	private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
