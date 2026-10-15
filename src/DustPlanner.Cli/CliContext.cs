using System.Text.Json;
using DustPlanner.Core;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Collection;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Json;
using DustPlanner.Core.Rules;

namespace DustPlanner.Cli;

/// <summary>Shared services for commands: paths, rules, catalog and collection loading.</summary>
internal sealed class CliContext
{
    private GameRules? _rules;

    public CliContext(AppPaths paths, TextWriter output, TextWriter error, bool json)
    {
        Paths = paths;
        Out = output;
        Err = error;
        Json = json;
    }

    public AppPaths Paths { get; }

    public TextWriter Out { get; }

    public TextWriter Err { get; }

    public bool Json { get; }

    public GameRules Rules => _rules ??= GameDataFiles.LoadRules(Directory.Exists(Paths.DataOverrides) ? Paths.DataOverrides : null);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public async Task<CatalogLoadResult> LoadCatalogAsync(bool forceRefresh, string locale, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DustPlanner/0.1 (+https://github.com/)");
        var provider = new HearthstoneJsonCatalogProvider(http, new HearthstoneJsonOptions { CacheDirectory = Paths.CatalogCache, Locale = locale });
        var result = await provider.GetAsync(forceRefresh, ct).ConfigureAwait(false);
        if (result.Warning is not null && !Json)
        {
            await Err.WriteLineAsync("warning: " + result.Warning).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Loads the collection from an explicit file (HDT export, HSReplay JSON or CSV — detected by content/extension)
    /// or from the exporter inbox.
    /// </summary>
    public CollectionSnapshot LoadCollection(string? file, string? account, int? dustOverride, CardCatalog catalog)
    {
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
                throw new CliException(reason ?? "No collection available.");
            }

            collection = HdtExportImporter.ImportFile(Path.Combine(selected.Directory, Contracts.ExportSchema.CollectionFileName), Rules.PremiumSlots);
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
            throw new CliException($"Collection file not found: {file}");
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

        throw new CliException("Unrecognised collection file: expected a DustPlanner export (with \"meta\"), an HSReplay JSON (with \"collection\") or a .csv.");
    }

    /// <summary>A deck given as a code, a pasted block, or a path to a file containing either.</summary>
    public static DeckCode ReadDeck(string deckArgument)
    {
        if (deckArgument == "-")
        {
            return DeckBlockParser.Parse(Console.In.ReadToEnd());
        }

        if (File.Exists(deckArgument))
        {
            return DeckBlockParser.Parse(File.ReadAllText(deckArgument));
        }

        return DeckBlockParser.Parse(deckArgument);
    }

    public Task WriteJsonAsync<T>(T value) => Out.WriteLineAsync(JsonSerializer.Serialize(value, CoreJson.Options));
}

internal sealed class CliException(string message) : Exception(message);
