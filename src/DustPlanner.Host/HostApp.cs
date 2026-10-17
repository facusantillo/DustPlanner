using System.Reflection;
using System.Text;
using System.Text.Json;
using DustPlanner.App;
using DustPlanner.Core.Json;
using DustPlanner.Host.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DustPlanner.Host;

/// <summary>Builds the web application: loopback-only Kestrel, the state service, the panel, the overlay and the JSON API.</summary>
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

        var services = new AppServices(options.Paths);
        var info = ApiEndpoints.DescribeService(options.Url, services.Paths.Root);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(services);
        builder.Services.AddSingleton<OverlayStateService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OverlayStateService>());
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DustPlanner.Kick.ChatResponder>();

        var app = builder.Build();

        // The panel (what the HDT plugin opens) and its assets.
        app.MapGet("/", () => Results.Content(Resource("Panel.index.html"), "text/html; charset=utf-8"));
        app.MapGet("/panel/app.css", () => Results.Content(Resource("Panel.app.css"), "text/css; charset=utf-8"));
        app.MapGet("/panel/app.js", () => Results.Content(Resource("Panel.app.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/panel/i18n.js", () => Results.Content(Resource("Panel.i18n.js"), "text/javascript; charset=utf-8"));

        // The OBS overlay and the host status page it had from the start.
        app.MapGet("/status", () => Results.Content(Resource("Overlay.index.html"), "text/html; charset=utf-8"));
        app.MapGet("/overlay", () => Results.Content(Resource("Overlay.overlay.html"), "text/html; charset=utf-8"));
        app.MapGet("/api/state", (OverlayStateService service) => Results.Content(JsonSerializer.Serialize(service.Current, CoreJson.Options), "application/json; charset=utf-8"));
        app.MapGet("/events", StreamEventsAsync);

        ApiEndpoints.Map(app, info);
        ChatEndpoints.Map(app);

        return app;
    }

    /// <summary>Server-sent events: the current state immediately, then every change; a comment line every 15 s keeps the connection alive.</summary>
    private static async Task StreamEventsAsync(HttpContext context, OverlayStateService service, CancellationToken ct)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(ct).ConfigureAwait(false);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var states = service.Subscribe(stop.Token).GetAsyncEnumerator(stop.Token);
        var next = states.MoveNextAsync().AsTask();
        try
        {
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
            // An async iterator cannot be disposed while a MoveNextAsync is pending: cancel it and let it finish first.
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await next.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            await states.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static readonly JsonSerializerOptions SseJson = new(CoreJson.Options) { WriteIndented = false };

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DustPlanner.Host." + name)
            ?? throw new InvalidOperationException("Missing embedded resource " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
