using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DustPlanner.Core.Json;

namespace DustPlanner.Core.Cards;

/// <summary>Where a catalog came from, so the CLI can say "cached build 12345 from 2026-10-01".</summary>
public sealed record CatalogLoadResult(CardCatalog Catalog, bool FromCache, DateTimeOffset? CachedAt, string? Warning);

public interface ICatalogProvider
{
    Task<CatalogLoadResult> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

/// <summary>Loads a catalog from a local HearthstoneJSON file (tests, offline use).</summary>
public sealed class FileCatalogProvider(string path, string build = "file", string locale = "enUS") : ICatalogProvider
{
    public async Task<CatalogLoadResult> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var catalog = HearthstoneJsonParser.Parse(stream, build, locale);
        return new CatalogLoadResult(catalog, FromCache: true, File.GetLastWriteTimeUtc(path), null);
    }
}

public sealed class HearthstoneJsonOptions
{
    public const string DefaultBaseAddress = "https://api.hearthstonejson.com/v1/";

    public Uri BaseAddress { get; set; } = new(DefaultBaseAddress);

    public string Locale { get; set; } = "enUS";

    /// <summary>Directory that holds <c>{build}/cards.{locale}.json</c> and <c>manifest.json</c>.</summary>
    public required string CacheDirectory { get; set; }

    /// <summary>How long a cached "latest build" answer is trusted before asking HearthstoneJSON again.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Downloads <c>cards.json</c> (every card: the collectible ones plus the tokens that Fabled legendaries put in a
/// deck) from HearthstoneJSON and caches it per client build. Card data is © Blizzard Entertainment and is never
/// redistributed; it is fetched on the user's machine.
/// </summary>
public sealed partial class HearthstoneJsonCatalogProvider : ICatalogProvider
{
    private readonly HttpClient _http;
    private readonly HearthstoneJsonOptions _options;
    private readonly TimeProvider _time;

    public HearthstoneJsonCatalogProvider(HttpClient http, HearthstoneJsonOptions options, TimeProvider? time = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
    }

    public async Task<CatalogLoadResult> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.CacheDirectory);
        var manifest = ReadManifest();
        var now = _time.GetUtcNow();

        if (!forceRefresh && manifest is not null && manifest.Locale == _options.Locale
            && now - manifest.CheckedAt < _options.MaxAge && File.Exists(CardFile(manifest.Build)))
        {
            return new CatalogLoadResult(await LoadFile(manifest.Build, cancellationToken).ConfigureAwait(false), true, manifest.CheckedAt, null);
        }

        string? warning = null;
        try
        {
            var build = await ResolveLatestBuildAsync(cancellationToken).ConfigureAwait(false);
            var file = CardFile(build);
            var unknownBuildStale = build == "latest" && File.Exists(file) && now - File.GetLastWriteTimeUtc(file) > _options.MaxAge;
            if (forceRefresh || !File.Exists(file) || unknownBuildStale)
            {
                await DownloadAsync(build, file, cancellationToken).ConfigureAwait(false);
            }

            WriteManifest(new CacheManifest(build, _options.Locale, now));
            return new CatalogLoadResult(await LoadFile(build, cancellationToken).ConfigureAwait(false), false, now, null);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException)
        {
            warning = $"Could not reach HearthstoneJSON ({ex.Message}).";
        }

        // Offline fallback: newest cached build, if any.
        var fallback = manifest?.Build is { } b && File.Exists(CardFile(b)) ? b : NewestCachedBuild();
        if (fallback is null)
        {
            throw new InvalidOperationException(warning + " No cached card catalog is available. Connect once to download it.");
        }

        return new CatalogLoadResult(await LoadFile(fallback, cancellationToken).ConfigureAwait(false), true, manifest?.CheckedAt,
            warning + $" Using cached build {fallback}.");
    }

    /// <summary>
    /// HearthstoneJSON answers <c>/v1/latest/...</c> with a redirect to <c>/v1/{build}/...</c>; the build number is taken
    /// from that redirect. When there is no redirect the literal <c>latest</c> is used as the cache key.
    /// </summary>
    private async Task<string> ResolveLatestBuildAsync(CancellationToken cancellationToken)
    {
        // GET with headers-only read: the body is discarded, the redirect (or the final URL) carries the build number.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseAddress, $"latest/{_options.Locale}/cards.json"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var isRedirect = (int)response.StatusCode is >= 300 and < 400;
        if (isRedirect && response.Headers.Location is { } location)
        {
            var match = BuildRegex().Match(location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString);
            if (match.Success)
            {
                return match.Groups["build"].Value;
            }
        }

        if (response.StatusCode == HttpStatusCode.OK && response.RequestMessage?.RequestUri is { } final)
        {
            var match = BuildRegex().Match(final.AbsolutePath);
            if (match.Success)
            {
                return match.Groups["build"].Value;
            }
        }

        if (!isRedirect)
        {
            response.EnsureSuccessStatusCode();
        }

        // HearthstoneJSON now serves /v1/latest/ directly (no redirect); its directory index links to /v1/{build}/...
        var index = await ReadIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index is not null)
        {
            var match = BuildRegex().Match(index);
            if (match.Success)
            {
                return match.Groups["build"].Value;
            }
        }

        return "latest";
    }

    private async Task<string?> ReadIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(_options.BaseAddress, "latest/"), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            // The index is a few KB of HTML; cap the read defensively.
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return text.Length > 1_000_000 ? text[..1_000_000] : text;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private async Task DownloadAsync(string build, string file, CancellationToken cancellationToken)
    {
        var segment = build == "latest" ? "latest" : build;
        var uri = new Uri(_options.BaseAddress, $"{segment}/{_options.Locale}/cards.json");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        await using (var target = File.Create(temp))
        {
            await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, file, overwrite: true);
    }

    private async Task<CardCatalog> LoadFile(string build, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(CardFile(build));
        return await Task.Run(() => HearthstoneJsonParser.Parse(stream, build, _options.Locale), cancellationToken).ConfigureAwait(false);
    }

    private string CardFile(string build) => Path.Combine(_options.CacheDirectory, build, $"cards.{_options.Locale}.json");

    private string ManifestFile => Path.Combine(_options.CacheDirectory, "manifest.json");

    private CacheManifest? ReadManifest()
    {
        try
        {
            return File.Exists(ManifestFile)
                ? JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(ManifestFile), CoreJson.Options)
                : null;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            return null;
        }
    }

    private void WriteManifest(CacheManifest manifest) =>
        File.WriteAllText(ManifestFile, JsonSerializer.Serialize(manifest, CoreJson.Options));

    private string? NewestCachedBuild()
    {
        if (!Directory.Exists(_options.CacheDirectory))
        {
            return null;
        }

        return Directory.GetDirectories(_options.CacheDirectory)
            .Select(Path.GetFileName)
            .Where(name => name is not null && File.Exists(CardFile(name)))
            .OrderByDescending(name => int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .FirstOrDefault();
    }

    [GeneratedRegex(@"/v1/(?<build>\d+)/", RegexOptions.CultureInvariant)]
    private static partial Regex BuildRegex();

    private sealed record CacheManifest(string Build, string Locale, DateTimeOffset CheckedAt);
}
