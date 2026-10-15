using System.Text.Json;
using System.Text.Json.Serialization;

namespace DustPlanner.Contracts.Tests;

/// <summary>
/// System.Text.Json options that mirror the exporter's Newtonsoft settings. The core will use the same
/// options (DustPlanner.Core.Json) — this copy keeps the contract tests independent of the core.
/// </summary>
internal static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.Strict,
    };
}
