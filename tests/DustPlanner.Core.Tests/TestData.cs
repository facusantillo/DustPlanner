using DustPlanner.Core.Cards;
using DustPlanner.Core.Rules;

namespace DustPlanner.Core.Tests;

internal static class TestData
{
    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Fixture(string name) => File.ReadAllText(FixturePath(name));

    public static CardCatalog SyntheticCatalog() => HearthstoneJsonParser.Parse(Fixture("cards.synthetic.json"), "synthetic", "enUS");

    public static GameRules Rules() => GameDataFiles.LoadRules();

    /// <summary>A date inside the SCARAB year (2026-03-17 .. next rotation).</summary>
    public static readonly DateOnly Today = new(2026, 10, 15);
}
