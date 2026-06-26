using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MyForce.Services;

internal sealed class What3WordsService
{
	private const string ConfigFileName = "myforce-ui.config.json";
	private const string ConfigDirectoryName = "myforce";
	private static readonly HttpClient HttpClient = CreateHttpClient();

	/// <summary>
	/// Resolves the what3words address for the provided coordinates when an API key is configured.
	/// </summary>
	public async Task<string?> GetWordsAsync(double latitude, double longitude, CancellationToken cancellationToken)
	{
		string? apiKey = GetConfiguredApiKey();
		if (string.IsNullOrWhiteSpace(apiKey))
		{
			return null;
		}

		string requestUri = string.Create(
			CultureInfo.InvariantCulture,
			$"https://api.what3words.com/v3/convert-to-3wa?coordinates={latitude:0.000000},{longitude:0.000000}&key={apiKey}&language=en");

		// The vehicle is regularly out of cellular/Wi-Fi coverage, so a failed lookup
		// must degrade to "no result" and never propagate. We catch transport faults
		// (no internet, DNS failure), the 10s client timeout, and malformed responses.
		try
		{
			using HttpResponseMessage response = await HttpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}

			await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
			using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
			if (!document.RootElement.TryGetProperty("words", out JsonElement wordsElement))
			{
				return null;
			}

			return wordsElement.GetString();
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or IOException)
		{
			// No connectivity / timeout / bad payload: treat as "no words available".
			return null;
		}
	}

	/// <summary>
	/// Loads the configured what3words API key from the UI config file if it exists.
	/// </summary>
	public string? GetConfiguredApiKey()
	{
		string configPath = ResolveConfigPath();
		if (!File.Exists(configPath))
		{
			return null;
		}

		// A missing, locked, or corrupt config file must not crash startup; treat any
		// read/parse failure as "no key configured" so the app still launches offline.
		try
		{
			using FileStream stream = File.OpenRead(configPath);
			using JsonDocument document = JsonDocument.Parse(stream);
			if (!document.RootElement.TryGetProperty("what3wordsApiKey", out JsonElement keyElement))
			{
				return null;
			}

			return keyElement.GetString();
		}
		catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// Saves the what3words API key to the UI config file so the dashboard can use it for lookups.
	/// </summary>
	public void SaveApiKey(string? apiKey)
	{
		string configPath = ResolveConfigPath();
		string? configDirectory = Path.GetDirectoryName(configPath);
		if (!string.IsNullOrWhiteSpace(configDirectory))
		{
			Directory.CreateDirectory(configDirectory);
		}

		JsonObject rootObject = LoadConfigObject(configPath);
		if (string.IsNullOrWhiteSpace(apiKey))
		{
			rootObject.Remove("what3wordsApiKey");
		}
		else
		{
			rootObject["what3wordsApiKey"] = apiKey.Trim();
		}

		File.WriteAllText(configPath, rootObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
	}

	/// <summary>
	/// Resolves the UI config file to a STABLE per-user location so the saved API key
	/// survives reboots. Under a kiosk/systemd launch the .NET ApplicationData folder can
	/// resolve empty (HOME-derived), which previously fell back to the app's install
	/// directory, an ephemeral path on the in-vehicle image, so the key appeared to be
	/// lost on every reboot. We now walk a chain of persistent candidates and only use
	/// the install directory as a last resort. The same resolver is used for save and
	/// load, so both always agree on the path.
	/// </summary>
	private static string ResolveConfigPath()
	{
		foreach (string? candidate in EnumerateConfigDirectoryCandidates())
		{
			if (!string.IsNullOrWhiteSpace(candidate))
			{
				return Path.Combine(candidate, ConfigDirectoryName, ConfigFileName);
			}
		}

		return Path.Combine(AppContext.BaseDirectory, ConfigFileName);
	}

	/// <summary>
	/// Ordered persistent base directories for the config file. ApplicationData (~/.config
	/// on Linux) stays first to preserve the existing path when HOME is set; UserProfile
	/// (~/.config) and the XDG/HOME environment values are recovery candidates for launches
	/// where ApplicationData resolves empty. Reading HOME/XDG here is OS path discovery, not
	/// runtime configuration, so it is consistent with the project's config-file policy.
	/// </summary>
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

	/// <summary>
	/// Loads the existing UI config object or returns an empty object when the file is missing or invalid.
	/// </summary>
	private static JsonObject LoadConfigObject(string configPath)
	{
		if (!File.Exists(configPath))
		{
			return [];
		}

		try
		{
			JsonNode? rootNode = JsonNode.Parse(File.ReadAllText(configPath));
			return rootNode as JsonObject ?? [];
		}
		catch (JsonException)
		{
			return [];
		}
	}

	/// <summary>
	/// Creates the shared HTTP client used for what3words lookups.
	/// </summary>
	private static HttpClient CreateHttpClient()
	{
		HttpClient client = new();
		client.DefaultRequestHeaders.UserAgent.ParseAdd("MyForce/1.0");
		client.Timeout = TimeSpan.FromSeconds(10);
		return client;
	}
}
