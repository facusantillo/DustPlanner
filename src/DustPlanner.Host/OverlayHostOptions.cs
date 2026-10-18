using DustPlanner.Core;

namespace DustPlanner.Host;

/// <summary>Command-line settings of the host. Listens on loopback only; nothing is ever exposed to the network.</summary>
public sealed record OverlayHostOptions
{
    public const int DefaultPort = 8765;

    /// <summary>How many consecutive ports are tried when the requested one is busy.</summary>
    public const int PortAttempts = 10;

    /// <summary>Written next to the database at start-up: the URL actually bound, the pid and the home folder.</summary>
    public const string ServiceFileName = "service.json";

    public string? Home { get; init; }

    public int Port { get; init; } = DefaultPort;

    /// <summary>Hearthstone's Logs folder; default: located through HDT's config / the usual install paths.</summary>
    public string? LogsDirectory { get; init; }

    /// <summary>Account key or BattleTag to show; default: the most recently exported account.</summary>
    public string? Account { get; init; }

    /// <summary>How often Power.log and the inbox are polled.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Open the panel in the default browser once listening (<c>--open</c>).</summary>
    public bool OpenBrowser { get; init; }

    /// <summary>The Kick relay (<c>--relay</c> or <c>DUSTPLANNER_RELAY_URL</c>); tests point it at an in-process fake.</summary>
    public string RelayUrl { get; init; } = Environment.GetEnvironmentVariable("DUSTPLANNER_RELAY_URL") is { Length: > 0 } relay ? relay : DustPlanner.Kick.KickConnectorOptions.DefaultRelayUrl;

    public AppPaths Paths => Home is null ? AppPaths.Default() : new AppPaths(Path.GetFullPath(Home));

    public string Url => $"http://127.0.0.1:{Port}";

    public static OverlayHostOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new OverlayHostOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--home": options = options with { Home = Next() }; break;
                case "--port": options = options with { Port = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture) }; break;
                case "--logs": options = options with { LogsDirectory = Next() }; break;
                case "--account": options = options with { Account = Next() }; break;
                case "--poll-seconds": options = options with { PollInterval = TimeSpan.FromSeconds(double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture)) }; break;
                case "--open": options = options with { OpenBrowser = true }; break;
                case "--relay": options = options with { RelayUrl = Next() }; break;
                case "--help" or "-h":
                    throw new ArgumentException("usage: dustplanner-host [--home <folder>] [--port 8765] [--logs <Hearthstone\\Logs>] [--account <key|BattleTag>] [--poll-seconds 2] [--relay <url>] [--open]");
                default:
                    throw new ArgumentException($"Unknown option {args[i]}");
            }
        }

        return options;
    }
}
