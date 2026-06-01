using System;
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

		using FileStream stream = File.OpenRead(configPath);
		using JsonDocument document = JsonDocument.Parse(stream);
		if (!document.RootElement.TryGetProperty("what3wordsApiKey", out JsonElement keyElement))
		{
			return null;
		}

		return keyElement.GetString();
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
	/// Resolves the UI config file location using the per-user application data directory first.
	/// </summary>
	private static string ResolveConfigPath()
	{
		string appConfigDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		if (!string.IsNullOrWhiteSpace(appConfigDirectory))
		{
			return Path.Combine(appConfigDirectory, ConfigDirectoryName, ConfigFileName);
		}

		return Path.Combine(AppContext.BaseDirectory, ConfigFileName);
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
