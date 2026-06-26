using System;
using System.Collections.Generic;
using System.IO;

namespace MyForce.Services;

/// <summary>
/// Resolves UI config file paths to a STABLE per-user location so settings survive
/// reboots. Mirrors the resolution What3WordsService uses: ApplicationData (~/.config
/// on Linux) first, then explicit HOME/XDG fallbacks for kiosk/systemd launches where
/// ApplicationData can resolve empty, and only the install dir as a last resort.
/// </summary>
internal static class UiConfigPaths
{
	private const string ConfigDirectoryName = "myforce";

	/// <summary>Full path to <paramref name="fileName"/> under the persistent myforce config dir.</summary>
	public static string Resolve(string fileName)
	{
		foreach (string? candidate in EnumerateConfigDirectoryCandidates())
		{
			if (!string.IsNullOrWhiteSpace(candidate))
			{
				return Path.Combine(candidate, ConfigDirectoryName, fileName);
			}
		}

		return Path.Combine(AppContext.BaseDirectory, fileName);
	}

	private static IEnumerable<string?> EnumerateConfigDirectoryCandidates()
	{
		yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

		string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		yield return string.IsNullOrWhiteSpace(userProfile) ? null : Path.Combine(userProfile, ".config");

		yield return Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

		string? home = Environment.GetEnvironmentVariable("HOME");
		yield return string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".config");

		yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
	}
}
