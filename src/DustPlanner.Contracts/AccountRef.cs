using System.Globalization;

namespace DustPlanner.Contracts;

/// <summary>Identifies the Battle.net account an export belongs to.</summary>
public sealed class AccountRef
{
    /// <summary>High part of the Battle.net account id (region is encoded in bits 32..39).</summary>
    public ulong Hi { get; set; }

    /// <summary>Low part of the Battle.net account id.</summary>
    public ulong Lo { get; set; }

    /// <summary>BattleTag as <c>Name#1234</c>, when the exporter could read it.</summary>
    public string? BattleTag { get; set; }

    /// <summary>Region name derived from <see cref="Hi"/>: <c>US</c>, <c>EU</c>, <c>ASIA</c>, <c>CHINA</c> or <c>UNKNOWN</c>.</summary>
    public string? Region { get; set; }

    /// <summary>Stable key used for folders and database rows: <c>{hi}-{lo}</c>.</summary>
    public string AccountKey
    {
        get => AccountKeys.Format(Hi, Lo);
        set
        {
            // Setter exists only so serializers can round-trip the value; it is derived from Hi/Lo.
            if (AccountKeys.TryParse(value, out var hi, out var lo))
            {
                Hi = hi;
                Lo = lo;
            }
        }
    }
}

/// <summary>Helpers for the <c>{hi}-{lo}</c> account key format.</summary>
public static class AccountKeys
{
    public static string Format(ulong hi, ulong lo) =>
        hi.ToString(CultureInfo.InvariantCulture) + "-" + lo.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string? key, out ulong hi, out ulong lo)
    {
        hi = 0;
        lo = 0;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var parts = key!.Split('-');
        return parts.Length == 2
            && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hi)
            && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out lo);
    }

    /// <summary>
    /// Region name encoded in the account id, using the same decoding HDT applies
    /// (<c>(hi &gt;&gt; 32) &amp; 0xFF</c>; 1 = US, 2 = EU, 3 = ASIA, 5 = CHINA).
    /// </summary>
    public static string RegionFromHi(ulong hi)
    {
        var code = (int)((hi >> 32) & 0xFF);
        return code switch
        {
            1 => "US",
            2 => "EU",
            3 => "ASIA",
            5 => "CHINA",
            _ => "UNKNOWN",
        };
    }
}
