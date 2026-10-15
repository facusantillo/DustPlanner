// HdtApiCheck — verifies that every HDT / HearthMirror / HearthDb API the exporter uses exists, with the
// expected signature, in a given HDT installation. Pure metadata inspection: nothing is executed.
//
//   dotnet run --project tools/HdtApiCheck -- <hdt-folder> [--netfx-ref <dir>] [--report <file.md>]
//
// Exit code 0 when every required API is present, 1 otherwise.

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

var hdtDir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ".";
var netfxRef = OptionValue(args, "--netfx-ref") ?? Environment.GetEnvironmentVariable("NETFX_REF") ?? FindNetFxReferenceAssemblies();
var reportPath = OptionValue(args, "--report");

if (netfxRef is null || !Directory.Exists(netfxRef))
{
    Console.Error.WriteLine("Could not find the .NET Framework 4.7.2 reference assemblies. Pass --netfx-ref <dir> or set NETFX_REF.");
    return 2;
}

var manifestPath = Path.Combine(AppContext.BaseDirectory, "required-apis.json");
var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

var paths = new List<string>();
paths.AddRange(Directory.GetFiles(netfxRef, "*.dll"));
var facades = Path.Combine(netfxRef, "Facades");
if (Directory.Exists(facades))
{
    paths.AddRange(Directory.GetFiles(facades, "*.dll"));
}

paths.AddRange(Directory.GetFiles(hdtDir, "*.dll"));
paths.AddRange(Directory.GetFiles(hdtDir, "*.exe"));

using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "mscorlib");
var assemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal)
{
    ["hdt"] = mlc.LoadFromAssemblyPath(Path.Combine(hdtDir, "Hearthstone Deck Tracker.exe")),
    ["mirror"] = mlc.LoadFromAssemblyPath(Path.Combine(hdtDir, "HearthMirror.dll")),
    ["db"] = mlc.LoadFromAssemblyPath(Path.Combine(hdtDir, "HearthDb.dll")),
};

var report = new StringBuilder();
report.AppendLine("# HDT API check");
report.AppendLine();
report.AppendLine($"- Folder: `{Path.GetFullPath(hdtDir)}`");
foreach (var (key, asm) in assemblies)
{
    report.AppendLine($"- {key}: `{asm.GetName().Name}` {asm.GetName().Version}");
}

report.AppendLine();
report.AppendLine("| Status | Assembly | Type | Member | Found signature | Used for |");
report.AppendLine("|---|---|---|---|---|---|");

var missing = 0;
foreach (var api in manifest.Apis)
{
    var type = assemblies[api.Assembly].GetType(api.Type);
    string status;
    string found;
    if (type is null)
    {
        status = "MISSING TYPE";
        found = "";
        missing++;
    }
    else
    {
        var members = type.GetMember(api.Member, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        var signatures = members.Select(Describe).ToList();
        var match = signatures.FirstOrDefault(s => s.Contains(api.Signature, StringComparison.Ordinal));
        if (match is not null)
        {
            status = "ok";
            found = match;
        }
        else if (signatures.Count > 0)
        {
            status = "CHANGED";
            found = string.Join(" / ", signatures);
            missing++;
        }
        else
        {
            status = "MISSING";
            found = "";
            missing++;
        }
    }

    report.AppendLine($"| {status} | {api.Assembly} | `{api.Type}` | `{api.Member}` | `{found}` | {api.UsedFor} |");
}

report.AppendLine();
report.AppendLine("## APIs expected to be absent");
report.AppendLine();
foreach (var absent in manifest.Absent)
{
    var type = assemblies[absent.Assembly].GetType(absent.Type);
    var regex = new Regex(absent.MemberPattern);
    var hits = type is null
        ? Array.Empty<string>()
        : type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => regex.IsMatch(m.Name))
            .Select(Describe)
            .ToArray();
    report.AppendLine($"- `{absent.Type}` members matching `{absent.MemberPattern}`: {(hits.Length == 0 ? "none" : string.Join(", ", hits.Select(h => "`" + h + "`")))}");
    report.AppendLine($"  - {absent.Note}");
}

report.AppendLine();
report.AppendLine(missing == 0 ? "**Result: all required APIs present.**" : $"**Result: {missing} required API(s) missing or changed.**");

Console.WriteLine(report.ToString());
if (reportPath is not null)
{
    File.WriteAllText(reportPath, report.ToString());
}

return missing == 0 ? 0 : 1;

static string Describe(MemberInfo m) => m switch
{
    MethodInfo mi => $"{mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
    PropertyInfo pi => $"{pi.PropertyType.Name} {pi.Name}",
    FieldInfo fi => $"{fi.FieldType.Name} {fi.Name}",
    EventInfo ei => $"event {ei.EventHandlerType?.Name} {ei.Name}",
    _ => m.ToString() ?? m.Name,
};

static string? OptionValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string? FindNetFxReferenceAssemblies()
{
    var candidates = new List<string>();
    var nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    var package = Path.Combine(nugetRoot, "microsoft.netframework.referenceassemblies.net472");
    if (Directory.Exists(package))
    {
        candidates.AddRange(Directory.GetDirectories(package).OrderByDescending(d => d, StringComparer.Ordinal)
            .Select(v => Path.Combine(v, "build", ".NETFramework", "v4.7.2")));
    }

    if (OperatingSystem.IsWindows())
    {
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.7.2"));
    }

    return candidates.FirstOrDefault(Directory.Exists);
}

internal sealed class Manifest
{
    public List<RequiredApi> Apis { get; set; } = new();

    public List<AbsentApi> Absent { get; set; } = new();
}

internal sealed class RequiredApi
{
    public string Assembly { get; set; } = "";

    public string Type { get; set; } = "";

    public string Member { get; set; } = "";

    public string Signature { get; set; } = "";

    public string UsedFor { get; set; } = "";
}

internal sealed class AbsentApi
{
    public string Assembly { get; set; } = "";

    public string Type { get; set; } = "";

    public string MemberPattern { get; set; } = "";

    public string Note { get; set; } = "";
}
