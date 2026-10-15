using DustPlanner.App;
using DustPlanner.Core.Cards;
using DustPlanner.Store;

namespace DustPlanner.Mcp;

/// <summary>
/// What the tools are allowed to touch: the catalog cache (read, refreshed only when missing), the exporter inbox
/// (read), the local database (opened read-only) and the user's prefs/meta/refunds files (read). There is no code
/// path here that writes to the database, the game, HDT or any file of the user's.
/// </summary>
internal sealed class ReadOnlyGateway(AppServices app) : IDisposable
{
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private readonly Dictionary<string, (CatalogLoadResult Result, DateTimeOffset LoadedAt)> _catalogs = new(StringComparer.OrdinalIgnoreCase);

    public AppServices App { get; } = app ?? throw new ArgumentNullException(nameof(app));

    /// <summary>Catalog per locale, kept in memory for a while (the cache file is a few tens of megabytes).</summary>
    public async Task<CatalogLoadResult> CatalogAsync(string locale, CancellationToken ct)
    {
        await _catalogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalogs.TryGetValue(locale, out var cached) && DateTimeOffset.UtcNow - cached.LoadedAt < CatalogTtl)
            {
                return cached.Result;
            }

            var loaded = await App.LoadCatalogAsync(forceRefresh: false, locale, ct).ConfigureAwait(false);
            _catalogs[locale] = (loaded, DateTimeOffset.UtcNow);
            return loaded;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    public void Dispose() => _catalogLock.Dispose();

    /// <summary>Opens the database read-only; throws a user-facing error when it does not exist yet.</summary>
    public DustStore OpenDatabase()
    {
        if (!File.Exists(App.Paths.Database))
        {
            throw new AppException($"No DustPlanner database yet at {App.Paths.Database}. Run 'dustplanner sync' (collection) or 'dustplanner games import' (match history) first.");
        }

        return DustStore.Open(App.Paths.Database, readOnly: true);
    }
}
