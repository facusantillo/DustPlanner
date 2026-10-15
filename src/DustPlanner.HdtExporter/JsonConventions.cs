using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DustPlanner.HdtExporter;

/// <summary>
/// Newtonsoft settings that produce the contract described in <see cref="DustPlanner.Contracts.ExportSchema"/>:
/// snake_case names, ISO dates with offset, explicit nulls, indented for readability.
/// The core reads the same shape with System.Text.Json (<c>JsonNamingPolicy.SnakeCaseLower</c>).
/// </summary>
internal static class JsonConventions
{
    public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
        ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() },
        NullValueHandling = NullValueHandling.Include,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateParseHandling = DateParseHandling.DateTimeOffset,
        Formatting = Formatting.Indented,
    };
}
