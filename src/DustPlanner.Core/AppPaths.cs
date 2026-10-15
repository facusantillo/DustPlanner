namespace DustPlanner.Core;

/// <summary>Where DustPlanner keeps its files. Everything lives under one root (default <c>%LocalAppData%\DustPlanner</c>).</summary>
public sealed class AppPaths
{
    public const string HomeVariable = "DUSTPLANNER_HOME";

    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    /// <summary>Exporter output: <c>inbox/&lt;account_key&gt;/*.json</c>.</summary>
    public string Inbox => Path.Combine(Root, "inbox");

    /// <summary>HearthstoneJSON cache: <c>catalog/&lt;build&gt;/cards.collectible.&lt;locale&gt;.json</c>.</summary>
    public string CatalogCache => Path.Combine(Root, "catalog");

    /// <summary>Optional overrides of the embedded rule files (<c>format-rotation.json</c>, ...).</summary>
    public string DataOverrides => Path.Combine(Root, "data");

    /// <summary>User files: <c>prefs.json</c>, <c>meta.txt</c>, <c>refunds.json</c>.</summary>
    public string Config => Root;

    public string Database => Path.Combine(Root, "dustplanner.db");

    public static AppPaths Default()
    {
        var env = Environment.GetEnvironmentVariable(HomeVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new AppPaths(Path.GetFullPath(env));
        }

        return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DustPlanner"));
    }
}
