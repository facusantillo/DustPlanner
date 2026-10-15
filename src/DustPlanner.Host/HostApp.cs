using System.Reflection;
using System.Text;
using System.Text.Json;
using DustPlanner.App;
using DustPlanner.Core.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DustPlanner.Host;

/// <summary>Builds the web application: loopback-only Kestrel, the state service and four endpoints.</summary>
public static class HostApp
{
    public static WebApplication Create(OverlayHostOptions options, bool quiet = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls(options.Url);
        builder.Logging.ClearProviders();
        if (!quiet)
        {
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new AppServices(options.Paths));
        builder.Services.AddSingleton<OverlayStateService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OverlayStateService>());

        var app = builder.Build();

        app.MapGet("/", () => Results.Content(Resource("index.html"), "text/html; charset=utf-8"));
        app.MapGet("/overlay", () => Results.Content(Resource("overlay.html"), "text/html; charset=utf-8"));
        app.MapGet("/api/state", (OverlayStateService service) => Results.Content(JsonSerializer.Serialize(service.Current, CoreJson.Options), "application/json; charset=utf-8"));
        app.MapGet("/events", StreamEventsAsync);

        return app;
    }

    /// <summary>Server-sent events: the current state immediately, then every change; a comment line every 15 s keeps the connection alive.</summary>
    private static async Task StreamEventsAsync(HttpContext context, OverlayStateService service, CancellationToken ct)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(ct).ConfigureAwait(false);

        var states = service.Subscribe(ct).GetAsyncEnumerator(ct);
        try
        {
            var next = states.MoveNextAsync().AsTask();
            while (true)
            {
                var keepAlive = Task.Delay(TimeSpan.FromSeconds(15), ct);
                if (await Task.WhenAny(next, keepAlive).ConfigureAwait(false) == keepAlive)
                {
                    await context.Response.WriteAsync(": keep-alive\n\n", ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (!await next.ConfigureAwait(false))
                {
                    break;
                }

                var json = JsonSerializer.Serialize(states.Current, SseJson);
                await context.Response.WriteAsync("event: state\ndata: " + json + "\n\n", ct).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                next = states.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException)
        {
            // client went away
        }
        finally
        {
            await states.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static readonly JsonSerializerOptions SseJson = new(CoreJson.Options) { WriteIndented = false };

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DustPlanner.Host.Overlay." + name)
            ?? throw new InvalidOperationException("Missing embedded resource " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
