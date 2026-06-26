// %%%%%%    @%%%%%@
//%%%%%%%%   %%%%%%%@
//@%%%%%%%@  %%%%%%%%%        @@      @@  @@@      @@@ @@@     @@@ @@@@@@@@@@   @@@@@@@@@
//%%%%%%%%@ @%%%%%%%%       @@@@@   @@@@ @@@@@   @@@@ @@@@   @@@@ @@@@@@@@@@@@@@@@@@@@@@@ @@@@
// @%%%%%%%%  %%%%%%%%%      @@@@@@  @@@@  @@@@  @@@@   @@@@@@@@@     @@@@    @@@@         @@@@
//  %%%%%%%%%  %%%%%%%%@     @@@@@@@ @@@@   @@@@@@@@     @@@@@@       @@@@    @@@@@@@@@@@  @@@@
//   %%%%%%%%@  %%%%%%%%%    @@@@@@@@@@@@     @@@@        @@@@@       @@@@    @@@@@@@@@@@  @@@@
//    %%%%%%%%@ @%%%%%%%%    @@@@ @@@@@@@     @@@@      @@@@@@@@      @@@@    @@@@         @@@@
//    @%%%%%%%%% @%%%%%%%%   @@@@   @@@@@     @@@@     @@@@@ @@@@@    @@@@    @@@@@@@@@@@@ @@@@@@@@@@
//     @%%%%%%%%  %%%%%%%%@  @@@@    @@@@     @@@@    @@@@     @@@@   @@@@    @@@@@@@@@@@@ @@@@@@@@@@@
//      %%%%%%%%@ @%%%%%%%%
//      @%%%%%%%%  @%%%%%%%%
//       %%%%%%%%   %%%%%%%@
//         %%%%%      %%%%
//
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace MyForce.Services;

internal sealed class MapTileService : IDisposable
{
	private static readonly HttpClient HttpClient = CreateHttpClient();

	private readonly string _cacheRoot;

	private readonly ConcurrentDictionary<string, Bitmap?> _memoryCache = new(StringComparer.Ordinal);

	private bool _disposed;

	public MapTileService()
	{
		_cacheRoot = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"MyForce",
			"MapCache");

		Directory.CreateDirectory(_cacheRoot);
	}

	public async Task<Bitmap?> GetTileAsync(int zoom, int x, int y, CancellationToken cancellationToken)
	{
		ThrowIfDisposed();

		string cacheKey = FormattableString.Invariant($"{zoom}/{x}/{y}");
		if (_memoryCache.TryGetValue(cacheKey, out Bitmap? cachedBitmap))
		{
			return cachedBitmap;
		}

		string tilePath = Path.Combine(_cacheRoot, zoom.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), $"{y}.png");
		if (File.Exists(tilePath))
		{
			Bitmap bitmap = await LoadBitmapAsync(tilePath, cancellationToken).ConfigureAwait(false);
			_memoryCache[cacheKey] = bitmap;
			return bitmap;
		}

		string tileUrl = FormattableString.Invariant($"https://tile.openstreetmap.org/{zoom}/{x}/{y}.png");

		// The vehicle is frequently offline, so a tile fetch must degrade to "no tile"
		// (the map keeps showing cached tiles) and never propagate. We catch transport
		// faults (no internet/DNS), the client timeout, and local cache-write errors.
		try
		{
			using HttpResponseMessage response = await HttpClient.GetAsync(tileUrl, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}

			byte[] tileBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
			Directory.CreateDirectory(Path.GetDirectoryName(tilePath)!);
			await File.WriteAllBytesAsync(tilePath, tileBytes, cancellationToken).ConfigureAwait(false);
			Bitmap downloadedBitmap = new(new MemoryStream(tileBytes));
			_memoryCache[cacheKey] = downloadedBitmap;
			return downloadedBitmap;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
		{
			// No connectivity / timeout / cache-write failure: no tile this round.
			return null;
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		foreach (Bitmap bitmap in _memoryCache.Values)
		{
			bitmap?.Dispose();
		}

		_memoryCache.Clear();
	}

	private static HttpClient CreateHttpClient()
	{
		HttpClient client = new();
		client.DefaultRequestHeaders.UserAgent.ParseAdd("MyForce/1.0");
		client.Timeout = TimeSpan.FromSeconds(10);
		return client;
	}

	private static async Task<Bitmap> LoadBitmapAsync(string path, CancellationToken cancellationToken)
	{
		await using FileStream stream = File.OpenRead(path);
		using MemoryStream memoryStream = new();
		await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
		memoryStream.Position = 0;
		return new Bitmap(memoryStream);
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}
}