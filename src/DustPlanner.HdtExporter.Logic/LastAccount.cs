using System;
using System.Globalization;

namespace DustPlanner.HdtExporter.Logic;

/// <summary>
/// The one-line file (<c>last-account.txt</c>) that remembers the account HearthMirror last reported, so exports that
/// do not need the game — the active deck chosen in HDT's own list, above all — still know which inbox folder is
/// theirs while Hearthstone is closed. Format: <c>hi TAB lo TAB battletag</c>; the tag may be empty.
/// </summary>
public static class LastAccount
{
    public const string FileName = "last-account.txt";

    private static readonly char[] Tab = { '\t' };

    public static string Serialize(ulong hi, ulong lo, string? battleTag) =>
        hi.ToString(CultureInfo.InvariantCulture) + "\t" + lo.ToString(CultureInfo.InvariantCulture) + "\t" + (battleTag ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>False for anything that is not a line this type wrote, or for an all-zero account.</summary>
    public static bool TryParse(string? text, out ulong hi, out ulong lo, out string? battleTag)
    {
        hi = 0;
        lo = 0;
        battleTag = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text!.Trim().Split(Tab, 3);
        if (parts.Length < 2
            || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hi)
            || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out lo)
            || (hi == 0 && lo == 0))
        {
            hi = 0;
            lo = 0;
            return false;
        }

        battleTag = parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null;
        return true;
    }
}
