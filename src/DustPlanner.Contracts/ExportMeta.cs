using System;

namespace DustPlanner.Contracts;

/// <summary>Envelope shared by every export file.</summary>
public sealed class ExportMeta
{
    public int SchemaVersion { get; set; } = ExportSchema.Version;

    /// <summary>One of the <c>*Kind</c> constants in <see cref="ExportSchema"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    public DateTimeOffset ExportedAt { get; set; }

    /// <summary>Version of the DustPlanner exporter assembly.</summary>
    public string ExporterVersion { get; set; } = string.Empty;

    /// <summary>Version of the HDT assembly the plugin was loaded into (null when it could not be read).</summary>
    public string? HdtVersion { get; set; }

    public string? HearthMirrorVersion { get; set; }

    public string? HearthDbVersion { get; set; }

    /// <summary>What caused the export: <c>collection_changed</c>, <c>mode_changed:HUB</c>, <c>manual</c>, ...</summary>
    public string Trigger { get; set; } = string.Empty;

    public AccountRef Account { get; set; } = new AccountRef();

    /// <summary>Capabilities the exporter detected at runtime; lets the core explain missing data.</summary>
    public ExporterCapabilities Capabilities { get; set; } = new ExporterCapabilities();
}

/// <summary>Which HDT APIs were available when the export ran. False means "degraded", not "error".</summary>
public sealed class ExporterCapabilities
{
    public bool Collection { get; set; }
    public bool InGameDecks { get; set; }
    public bool HdtDecks { get; set; }
    public bool Rank { get; set; }
    public bool BattleTag { get; set; }
    public bool RawMirrorCollection { get; set; }
}
