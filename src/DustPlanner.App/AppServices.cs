using System.Globalization;
using System.Text.Json;
using DustPlanner.Contracts;
using DustPlanner.Core;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Planner;
using DustPlanner.Core.Rules;
using DustPlanner.Store;

namespace DustPlanner.App;

/// <summary>
/// Everything the front ends (CLI, MCP server, overlay host) share: where files live, how the catalog, collection,
/// decks, preferences and meta are loaded, and how a plan is produced. Reads files and the database; the only thing
/// it ever writes is the catalog cache.
/// </summary>
public sealed class AppServices
{
    private GameRules? _rules;

    public AppServices(AppPaths paths)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public AppPaths Paths { get; }

    /// <summary>Rule tables (embedded defaults, overridden by files in <c>&lt;home&gt;/data</c>).</summary>
    public GameRules Rules => _rules ??= GameDataFiles.LoadRules(Directory.Exists(Paths.DataOverrides) ? Paths.DataOverrides : null);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Resolves <c>--home</c> / <c>DUSTPLANNER_HOME</c> / the default folder.</summary>
    public static AppServices ForHome(string? home) =>
        new(home is null ? AppPaths.Default() : new AppPaths(Path.GetFullPath(home)));

    /// <summary>Loads the HearthstoneJSON catalog from the cache, downloading it when missing (or when forced).</summary>
    public async Task<CatalogLoadResult> LoadCatalogAsync(bool forceRefresh, string locale, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DustPlanner/0.1 (+https://github.com/)");
        var provider = new HearthstoneJsonCatalogProvider(http, new HearthstoneJsonOptions { CacheDirectory = Paths.CatalogCache, Locale = locale });
        return await provider.GetAsync(forceRefresh, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the collection from an explicit file (DustPlanner export, HSReplay JSON or CSV — detected by content and
    /// extension) or from the exporter inbox (<paramref name="account"/>: key or BattleTag; null = the only / latest one).
    /// </summary>
    public CollectionSnapshot LoadCollection(string? file, string? account, int? dustOverride, CardCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        CollectionSnapshot collection;
        if (file is not null)
        {
            collection = LoadCollectionFile(file, dustOverride, catalog);
        }
        else
        {
            var accounts = InboxLocator.ListAccounts(Paths.Inbox);
            var (selected, reason) = InboxLocator.Select(accounts, account);
            if (selected is null)
            {
                throw new AppException(reason ?? "No collection available.");
            }

            collection = HdtExportImporter.ImportFile(Path.Combine(selected.Directory, ExportSchema.CollectionFileName), Rules.PremiumSlots);
        }

        if (dustOverride is { } dust)
        {
            collection = new CollectionSnapshot(collection.AccountKey, collection.TakenAt, collection.Source, dust, collection.Cards,
                collection.Gold, collection.UnopenedPacks, collection.BattleTag, collection.Region);
        }

        return collection;
    }

    private CollectionSnapshot LoadCollectionFile(string file, int? dustOverride, CardCatalog catalog)
    {
        if (!File.Exists(file))
        {
            throw new AppException($"Collection file not found: {file}");
        }

        if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return CsvCollectionImporter.Import(File.ReadAllText(file), catalog, dustOverride ?? 0, takenAt: File.GetLastWriteTimeUtc(file));
        }

        var text = File.ReadAllText(file);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("meta", out _))
        {
            return HdtExportImporter.Import(text, Rules.PremiumSlots);
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("collection", out _))
        {
            return HsReplayImporter.Import(text, Rules.PremiumSlots, takenAt: File.GetLastWriteTimeUtc(file));
        }

        throw new AppException("Unrecognised collection file: expected a DustPlanner export (with \"meta\"), an HSReplay JSON (with \"collection\") or a .csv.");
    }

    /// <summary>A deck given as a code, a pasted block, or a path to a file containing either.</summary>
    public static DeckCode ParseDeck(string deckArgument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deckArgument);
        try
        {
            return File.Exists(deckArgument) ? DeckBlockParser.Parse(File.ReadAllText(deckArgument)) : DeckBlockParser.Parse(deckArgument);
        }
        catch (InvalidDeckstringException ex)
        {
            throw new AppException("Invalid deck code: " + ex.Message);
        }
    }

    public UserPrefs LoadPrefs(string? path) => UserPrefs.LoadOrDefault(path ?? Path.Combine(Paths.Config, UserPrefs.FileName));

    public MetaSignal LoadMeta(string? path, CardCatalog catalog)
    {
        var file = path ?? Path.Combine(Paths.Config, MetaSignal.FileName);
        return File.Exists(file) ? MetaSignal.FromText(File.ReadAllText(file), catalog) : MetaSignal.Empty;
    }

    public Refunds LoadRefunds(string? path) => Refunds.LoadOrEmpty(path ?? Path.Combine(Paths.Config, Refunds.FileName));

    /// <summary>
    /// Decks to protect: an explicit decks file, else the <c>decks.json</c> next to an explicit collection file, else
    /// the account's inbox, else the decks previously imported into the database by <c>sync</c>.
    /// </summary>
    public IReadOnlyList<ProtectedDeck> LoadSavedDecks(string? decksFile, string? collectionFile, string accountKey, UserPrefs prefs)
    {
        if (decksFile is not null)
        {
            if (!File.Exists(decksFile))
            {
                throw new AppException($"Decks file not found: {decksFile}");
            }

            return ProtectedDecks.FromExportFile(decksFile, prefs);
        }

        if (collectionFile is not null)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(collectionFile)) ?? ".", ExportSchema.DecksFileName);
            return File.Exists(sibling) ? ProtectedDecks.FromExportFile(sibling, prefs) : Array.Empty<ProtectedDeck>();
        }

        var inbox = Path.Combine(Paths.Inbox, accountKey, ExportSchema.DecksFileName);
        if (File.Exists(inbox))
        {
            return ProtectedDecks.FromExportFile(inbox, prefs);
        }

        if (File.Exists(Paths.Database))
        {
            using var store = DustStore.Open(Paths.Database, readOnly: true);
            return new DeckRepository(store).ProtectedDecks(accountKey, prefs);
        }

        return Array.Empty<ProtectedDeck>();
    }

    /// <summary>Missing-cards report for a deck.</summary>
    public async Task<NeedOutcome> NeedAsync(NeedOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var deck = ParseDeck(options.Deck);
        var loaded = await LoadCatalogAsync(forceRefresh: false, options.Locale, ct).ConfigureAwait(false);
        var collection = LoadCollection(options.CollectionFile, options.Account, options.DustOverride, loaded.Catalog);
        var asOf = options.AsOf ?? Today;
        var report = MissingCards.Analyze(deck, collection, loaded.Catalog, Rules, asOf);
        return new NeedOutcome(deck, report, collection, loaded);
    }

    /// <summary>Missing-cards report plus the disenchant plan. Nothing is persisted; the CLI stores plans itself.</summary>
    public async Task<PlanOutcome> PlanAsync(PlanOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var need = await NeedAsync(options, ct).ConfigureAwait(false);
        var catalog = need.Catalog.Catalog;
        var prefs = LoadPrefs(options.PrefsFile);
        var meta = LoadMeta(options.MetaFile, catalog);
        var refunds = LoadRefunds(options.RefundsFile);
        var savedDecks = LoadSavedDecks(options.DecksFile, options.CollectionFile, need.Collection.AccountKey, prefs);
        var manual = options.ProtectDeckCodes.Select((code, i) =>
        {
            var parsed = DeckBlockParser.Parse(code);
            return ProtectedDeck.FromDeckCode(parsed, parsed.Name ?? $"manual deck {i + 1}", ProtectedDecks.ManualOrigin, (i + 1).ToString(CultureInfo.InvariantCulture));
        });
        var protectedDecks = savedDecks.Concat(manual).ToList();

        var plan = DisenchantPlanner.Plan(new PlanRequest(need.Deck, need.Report, need.Collection, catalog, Rules, prefs, protectedDecks, meta, refunds, need.Report.AsOf)
        {
            NeedOverride = options.NeedOverride,
            AllowNoDecks = options.AllowNoDecks,
        });

        return new PlanOutcome(need, plan);
    }
}

/// <summary>Inputs of <see cref="AppServices.NeedAsync"/>.</summary>
public record NeedOptions(string Deck)
{
    public string? CollectionFile { get; init; }

    public string? Account { get; init; }

    public int? DustOverride { get; init; }

    public string Locale { get; init; } = "enUS";

    public DateOnly? AsOf { get; init; }
}

/// <summary>Inputs of <see cref="AppServices.PlanAsync"/>.</summary>
public sealed record PlanOptions(string Deck) : NeedOptions(Deck)
{
    public int? NeedOverride { get; init; }

    public string? DecksFile { get; init; }

    public IReadOnlyList<string> ProtectDeckCodes { get; init; } = Array.Empty<string>();

    public string? PrefsFile { get; init; }

    public string? MetaFile { get; init; }

    public string? RefundsFile { get; init; }

    public bool AllowNoDecks { get; init; }
}

public sealed record NeedOutcome(DeckCode Deck, MissingReport Report, CollectionSnapshot Collection, CatalogLoadResult Catalog);

public sealed record PlanOutcome(NeedOutcome Need, DisenchantPlan Plan);
