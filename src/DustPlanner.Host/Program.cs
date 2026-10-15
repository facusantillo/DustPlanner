using System.Diagnostics;
using System.Text.Json;
using DustPlanner.Core.Json;
using DustPlanner.Host;
using Microsoft.AspNetCore.Builder;

OverlayHostOptions options;
try
{
    options = OverlayHostOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

// Bind to the requested port, or the next free one of a short range: the HDT plugin reads the real URL from service.json.
WebApplication? app = null;
var port = options.Port;
for (var attempt = 0; attempt < OverlayHostOptions.PortAttempts; attempt++)
{
    var candidate = options with { Port = port + attempt };
    var built = HostApp.Create(candidate);
    try
    {
        await built.StartAsync().ConfigureAwait(false);
        app = built;
        options = candidate;
        break;
    }
    catch (IOException ex) when (attempt + 1 < OverlayHostOptions.PortAttempts)
    {
        Console.Error.WriteLine($"Port {candidate.Port} is busy ({ex.Message}); trying {candidate.Port + 1}.");
        await built.DisposeAsync().ConfigureAwait(false);
    }
}

if (app is null)
{
    Console.Error.WriteLine($"No free port between {options.Port} and {options.Port + OverlayHostOptions.PortAttempts - 1}.");
    return 1;
}

var serviceFile = Path.Combine(options.Paths.Root, OverlayHostOptions.ServiceFileName);
try
{
    Directory.CreateDirectory(options.Paths.Root);
    File.WriteAllText(serviceFile, JsonSerializer.Serialize(new { url = options.Url, pid = Environment.ProcessId, home = options.Paths.Root, started_at = DateTimeOffset.UtcNow }, CoreJson.Options));
}
catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Could not write {serviceFile}: {ex.Message}");
}

Console.WriteLine($"DustPlanner service listening on {options.Url} (loopback only)");
Console.WriteLine($"  panel:                           {options.Url}/");
Console.WriteLine($"  overlay for OBS Browser Source:  {options.Url}/overlay   (1920x1080, transparent)");
Console.WriteLine($"  status page:                     {options.Url}/status");

if (options.OpenBrowser)
{
    try
    {
        using var _ = Process.Start(new ProcessStartInfo(options.Url + "/") { UseShellExecute = true });
    }
    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is PlatformNotSupportedException)
    {
        Console.Error.WriteLine("Could not open the browser: " + ex.Message);
    }
}

await app.WaitForShutdownAsync().ConfigureAwait(false);

try
{
    File.Delete(serviceFile);
}
catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
{
    // best effort: a stale file only says the service was here
}

return 0;
