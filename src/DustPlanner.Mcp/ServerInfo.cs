using System.Reflection;

namespace DustPlanner.Mcp;

internal static class ServerInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";

    public const string Instructions =
        "DustPlanner is a read-only Hearthstone dust planner. These tools read the collection exported by the Hearthstone Deck Tracker " +
        "plugin, the local DustPlanner database and the games parsed from Hearthstone's own Power.log. Nothing here can craft, " +
        "disenchant, click, or change anything in the game: a disenchant plan is a recommendation the user applies by hand. " +
        "Inputs named 'deck' accept a deck code (AAECA...) or the deck block the game copies to the clipboard. " +
        "Dust values of signature/diamond copies and refund windows are conservative: unknown values are excluded and reported, not guessed.";
}
