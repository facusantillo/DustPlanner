using DustPlanner.Host;

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

var app = HostApp.Create(options);
Console.WriteLine($"DustPlanner host listening on {options.Url} (loopback only)");
Console.WriteLine($"  overlay for OBS Browser Source: {options.Url}/overlay   (1920x1080, transparent)");
Console.WriteLine($"  status page:                     {options.Url}/");
await app.RunAsync().ConfigureAwait(false);
return 0;
