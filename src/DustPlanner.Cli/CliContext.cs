using System.Text.Json;
using DustPlanner.App;
using DustPlanner.Core;
using DustPlanner.Core.Cards;
using DustPlanner.Core.Deckstrings;
using DustPlanner.Core.Json;
using DustPlanner.Core.Rules;

namespace DustPlanner.Cli;

/// <summary>Console-side wrapper over <see cref="AppServices"/>: output streams, JSON mode, stdin decks, catalog warnings.</summary>
internal sealed class CliContext
{
    public CliContext(AppPaths paths, TextWriter output, TextWriter error, bool json)
    {
        App = new AppServices(paths);
        Out = output;
        Err = error;
        Json = json;
    }

    public AppServices App { get; }

    public AppPaths Paths => App.Paths;

    public TextWriter Out { get; }

    public TextWriter Err { get; }

    public bool Json { get; }

    public GameRules Rules => App.Rules;

    public static DateOnly Today => AppServices.Today;

    /// <summary>Loads the catalog and prints its warning (stale cache, download failure) on stderr in text mode.</summary>
    public async Task<CatalogLoadResult> LoadCatalogAsync(bool forceRefresh, string locale, CancellationToken ct)
    {
        var result = await App.LoadCatalogAsync(forceRefresh, locale, ct).ConfigureAwait(false);
        if (result.Warning is not null && !Json)
        {
            await Err.WriteLineAsync("warning: " + result.Warning).ConfigureAwait(false);
        }

        return result;
    }

    public Core.Collection.CollectionSnapshot LoadCollection(string? file, string? account, int? dustOverride, CardCatalog catalog) =>
        App.LoadCollection(file, account, dustOverride, catalog);

    /// <summary>A deck given as a code, a pasted block, a path to a file containing either, or '-' for stdin.</summary>
    public static DeckCode ReadDeck(string deckArgument)
    {
        if (deckArgument == "-")
        {
            try
            {
                return DeckBlockParser.Parse(Console.In.ReadToEnd());
            }
            catch (InvalidDeckstringException ex)
            {
                throw new AppException("Invalid deck code: " + ex.Message);
            }
        }

        return AppServices.ParseDeck(deckArgument);
    }

    /// <summary>The deck argument in the text form the app services accept ('-' reads stdin once).</summary>
    public static string DeckText(string deckArgument) => deckArgument == "-" ? Console.In.ReadToEnd() : deckArgument;

    public Task WriteJsonAsync<T>(T value) => Out.WriteLineAsync(JsonSerializer.Serialize(value, CoreJson.Options));
}
