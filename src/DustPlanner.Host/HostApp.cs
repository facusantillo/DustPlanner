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
        var services = new AppServices(options.Paths);
        if (!quiet)
        {
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
            // The plugin starts the service hidden: service.log in the data folder is where its lines end up.
            builder.Logging.AddProvider(new FileLoggerProvider(services.Paths.Root));
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        }

        var info = ApiEndpoints.DescribeService(options.Url, services.Paths.Root);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(services);
        builder.Services.AddSingleton<OverlayStateService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OverlayStateService>());
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DustPlanner.Kick.ChatResponder>();
        builder.Services.AddSingleton<DustPlanner.Kick.IDeviceTokenStore>(new DustPlanner.Kick.FileDeviceTokenStore(services.Paths.Root));
        builder.Services.AddSingleton(new DustPlanner.Kick.KickConnectorOptions { RelayUrl = options.RelayUrl });
        builder.Services.AddSingleton(sp => new DustPlanner.Kick.KickConnector(
            sp.GetRequiredService<DustPlanner.Kick.KickConnectorOptions>(),
            sp.GetRequiredService<DustPlanner.Kick.IDeviceTokenStore>(),
            () =>
            {
                var prefs = services.ReadPrefs();
                prefs.Chat.Validate();
                prefs.Kick.Validate();
                return (prefs.Chat, prefs.Kick);
            },
            sp.GetRequiredService<ILogger<DustPlanner.Kick.KickConnector>>(),
            clock: sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DustPlanner.Kick.KickConnector>());
        // Stage K5: commands the relay forwards are answered here, with the same responder (and cooldowns) as /api/chat.
        builder.Services.AddSingleton(sp => new DustPlanner.Kick.KickChatBridge(
            sp.GetRequiredService<DustPlanner.Kick.KickConnector>(),
            sp.GetRequiredService<DustPlanner.Kick.ChatResponder>(),
            () => ChatEndpoints.ReadPrefs(services).Chat,
            () =>
            {
                var connector = sp.GetRequiredService<DustPlanner.Kick.KickConnector>();
                return ChatContextFactory.From(sp.GetRequiredService<OverlayStateService>().Current, KickDeckPublisher.PublicDeckUrl(connector, ChatEndpoints.ReadPrefs(services).Kick));
            },
            sp.GetRequiredService<ILogger<DustPlanner.Kick.KickChatBridge>>(),
            sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DustPlanner.Kick.KickChatBridge>());
        // Stage K6: the active deck travels to the relay while the public page is on.
        builder.Services.AddSingleton<KickDeckPublisher>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<KickDeckPublisher>());

        var app = builder.Build();

        // The panel (what the HDT plugin opens) and its assets. The assets carry the service version in their URL and are
        // never cached blindly: after an update the browser must not keep showing the previous panel (VERIFICACION K6).
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        app.MapGet("/", () => Asset(Resource("Panel.index.html").Replace("{{version}}", version, StringComparison.Ordinal), "text/html; charset=utf-8"));
        app.MapGet("/panel/app.css", () => Asset(Resource("Panel.app.css"), "text/css; charset=utf-8"));
        app.MapGet("/panel/app.js", () => Asset(Resource("Panel.app.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/panel/i18n.js", () => Asset(Resource("Panel.i18n.js"), "text/javascript; charset=utf-8"));

        // The OBS overlay and the host status page it had from the start.
        app.MapGet("/status", () => Results.Content(Resource("Overlay.index.html"), "text/html; charset=utf-8"));
        app.MapGet("/overlay", () => Results.Content(Resource("Overlay.overlay.html"), "text/html; charset=utf-8"));
        app.MapGet("/api/state", (OverlayStateService service) => Results.Content(JsonSerializer.Serialize(service.Current, CoreJson.Options), "application/json; charset=utf-8"));
        app.MapGet("/events", StreamEventsAsync);

        ApiEndpoints.Map(app, info);
        ChatEndpoints.Map(app);
        KickEndpoints.Map(app);

        return app;
    }

    /// <summary>Server-sent events: the current state immediately, then every change; a comment line every 15 s keeps the connection alive.</summary>
    private static async Task StreamEventsAsync(HttpContext context, OverlayStateService service, DustPlanner.Kick.KickConnector connector, CancellationToken ct)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.StartAsync(ct).ConfigureAwait(false);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var states = service.Subscribe(stop.Token).GetAsyncEnumerator(stop.Token);
        var next = states.MoveNextAsync().AsTask();

        // The Kick connection travels on the same stream as `kick` events: current state first, then every change.
        var kick = System.Threading.Channels.Channel.CreateBounded<DustPlanner.Kick.KickConnectionState>(new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });
        void OnKick(DustPlanner.Kick.KickConnectionState state) => kick.Writer.TryWrite(state);
        connector.StateChanged += OnKick;
        kick.Writer.TryWrite(connector.Current);
        var nextKick = kick.Reader.ReadAsync(stop.Token).AsTask();
        try
        {
            while (true)
            {
                var keepAlive = Task.Delay(TimeSpan.FromSeconds(15), ct);
                var done = await Task.WhenAny(next, nextKick, keepAlive).ConfigureAwait(false);
                if (done == keepAlive)
                {
                    await context.Response.WriteAsync(": keep-alive\n\n", ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (done == nextKick)
                {
                    var kickJson = JsonSerializer.Serialize(await nextKick.ConfigureAwait(false), SseJson);
                    await context.Response.WriteAsync("event: kick\ndata: " + kickJson + "\n\n", ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    nextKick = kick.Reader.ReadAsync(stop.Token).AsTask();
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
            connector.StateChanged -= OnKick;
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

    /// <summary>An embedded asset with <c>Cache-Control: no-cache</c>: revalidated on every load, which loopback makes free.</summary>
    private static NoCacheResult Asset(string content, string contentType) => new(Results.Content(content, contentType));

    private sealed class NoCacheResult(IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.CacheControl = "no-cache";
            return inner.ExecuteAsync(httpContext);
        }
    }

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DustPlanner.Host." + name)
            ?? throw new InvalidOperationException("Missing embedded resource " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
