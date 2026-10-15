using System.Collections.Generic;

namespace DustPlanner.Contracts;

/// <summary>Ranked ladder standing per format, from HearthMirror's <c>MedalInfo</c>.</summary>
public sealed class RankExport
{
    public ExportMeta Meta { get; set; } = new ExportMeta();

    /// <summary>Keyed by <see cref="RankFormats"/> names.</summary>
    public Dictionary<string, RankInfo> Formats { get; set; } = new Dictionary<string, RankInfo>();
}

public sealed class RankInfo
{
    /// <summary>League id as the client reports it (Bronze..Diamond / Legend mapping is done by the core).</summary>
    public int LeagueId { get; set; }

    public int StarLevel { get; set; }

    public int Stars { get; set; }

    public int StarsPerWin { get; set; }

    /// <summary>0 when not Legend.</summary>
    public int LegendRank { get; set; }

    public int SeasonId { get; set; }

    public int SeasonGames { get; set; }

    public int SeasonWins { get; set; }

    public int Streak { get; set; }

    public int BestStarLevel { get; set; }

    public int BestEverLeagueId { get; set; }

    public int BestEverStarLevel { get; set; }
}

public static class RankFormats
{
    public const string Standard = "standard";
    public const string Wild = "wild";
    public const string Classic = "classic";
    public const string Twist = "twist";
}
