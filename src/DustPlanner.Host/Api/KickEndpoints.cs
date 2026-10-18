using System.Text.Json;
using DustPlanner.Core.Json;
using DustPlanner.Kick;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DustPlanner.Host.Api;

/// <summary>
/// The native Kick connection (PRD §5.4): pairing through the browser, status for the panel, disconnect. Loopback only,
/// like everything else. The browser lands on <c>/kick/paired</c> straight from the relay's callback page.
/// </summary>
internal static class KickEndpoints
{
    private static readonly JsonSerializerOptions Json = CoreJson.Options;

    public static void Map(WebApplication app)
    {
        app.MapGet("/kick/paired", async (HttpContext http, KickConnector connector, string? code, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return Page(400, "Falta el código / The code is missing.", "Volvé al panel y probá de nuevo. / Go back to the panel and try again.");
            }

            if (!connector.HasPendingPairing)
            {
                // Nobody pressed Connect here: a stray or replayed link must not pair this service.
                return Page(409, "No hay un emparejamiento en curso. / No pairing is in progress.", "Abrí el panel, Settings → Kick, y apretá Conectar con Kick. / Open the panel, Settings → Kick, and press Connect with Kick.");
            }

            try
            {
                var pairing = await connector.CompletePairingAsync(code, ct).ConfigureAwait(false);
                return Page(200, $"Listo: DustPlanner quedó conectado con @{pairing.Channel.Slug}. / Done: DustPlanner is connected to @{pairing.Channel.Slug}.", "Ya podés cerrar esta pestaña. / You can close this tab.", PanelUrl(http));
            }
            catch (KickPairingException ex)
            {
                return Page(400, "No se pudo emparejar. / Pairing failed.", ex.Message, PanelUrl(http));
            }
        });

        var api = app.MapGroup("/api/kick");

        api.MapGet("/status", (KickConnector connector) => Ok(Describe(connector)));

        // Returns the URL the panel opens in a new tab; the service never launches a browser from a request handler.
        api.MapPost("/connect", (HttpContext http, KickConnector connector) =>
        {
            var port = http.Request.Host.Port ?? OverlayHostOptions.DefaultPort;
            var url = connector.BeginPairing(port);
            return Ok(new { connect_url = url, expires_at = connector.Current.PairingExpiresAt, relay_url = connector.RelayUrl, pairing_pending = connector.HasPendingPairing, state = connector.Current });
        });

        api.MapPost("/pair", async (HttpContext http, KickConnector connector, CancellationToken ct) =>
        {
            PairRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<PairRequest>(http.Request.Body, Json, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (string.IsNullOrWhiteSpace(request?.Code))
            {
                return Error(400, "Paste the code shown by the Kick page.");
            }

            try
            {
                await connector.CompletePairingAsync(request.Code, ct).ConfigureAwait(false);
                return Ok(Describe(connector));
            }
            catch (KickPairingException ex)
            {
                return Error(400, ex.Message);
            }
        });

        api.MapPost("/disconnect", async (KickConnector connector, CancellationToken ct) =>
        {
            await connector.DisconnectAsync(ct).ConfigureAwait(false);
            return Ok(Describe(connector));
        });
    }

    public static object Describe(KickConnector connector) => new
    {
        relay_url = connector.RelayUrl,
        pairing_pending = connector.HasPendingPairing,
        state = connector.Current,
    };

    private static IResult Ok(object payload) => Results.Content(JsonSerializer.Serialize(payload, Json), "application/json; charset=utf-8");

    private static IResult Error(int status, string message) =>
        Results.Content(JsonSerializer.Serialize(new { error = message }, Json), "application/json; charset=utf-8", statusCode: status);

    private static string PanelUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}/#/settings";

    private static IResult Page(int status, string title, string detail, string? panelUrl = null)
    {
        var link = panelUrl is null ? string.Empty : $"<p><a href=\"{Escape(panelUrl)}\">Abrir el panel / Open the panel</a></p>";
        const string style = "body{margin:0;min-height:100vh;display:grid;place-items:center;background:#15161b;color:#f1f1ee;font-family:\"Segoe UI\",system-ui,sans-serif}"
            + "main{max-width:520px;padding:32px 28px;background:#1f2128;border-radius:16px}h1{font-size:20px;margin:0 0 12px}p{line-height:1.5;color:#cfd0cc}a{color:#9fc5ff}";
        var html = "<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>DustPlanner · Kick</title>"
            + "<style>" + style + "</style></head><body><main><h1>" + Escape(title) + "</h1><p>" + Escape(detail) + "</p>" + link + "</main></body></html>";
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }

    private static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);

    private sealed class PairRequest
    {
        public string? Code { get; set; }
    }
}
