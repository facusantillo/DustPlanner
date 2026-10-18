using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DustPlanner.Kick;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DustPlanner.Kick.Tests;

/// <summary>
/// An in-process stand-in for <c>live.dustplanner.app</c>: the three routes the connector uses, speaking the protocol
/// of PRD §5.3, with knobs to misbehave on purpose. Nothing here reaches the network.
/// </summary>
public sealed class FakeRelay : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, string> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _devices = new(StringComparer.Ordinal);
    private readonly List<WebSocket> _sockets = new();
    private readonly ConcurrentQueue<JsonObject> _received = new();
    private readonly SemaphoreSlim _receivedSignal = new(0);
    private readonly SemaphoreSlim _connectedSignal = new(0);

    public FakeRelay()
    {
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRoutingCore();
        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapPost("/pair/exchange", PairExchange);
        _app.MapGet("/ws", WebSocketEndpoint);
        _app.MapPost("/disconnect", Disconnect);
    }

    public string Url { get; private set; } = string.Empty;

    public string ChannelUserId { get; set; } = "12345";

    public string Slug { get; set; } = "tsukuru_tv";

    public string Username { get; set; } = "Tsukuru";

    /// <summary>Answer every <c>/ws</c> with 401 (the relay forgot the device).</summary>
    public bool RejectSockets { get; set; }

    /// <summary>Send <c>needs_reconnect: true</c> in the hello.</summary>
    public bool HelloNeedsReconnect { get; set; }

    public int Connections { get; private set; }

    public int DisconnectCalls { get; private set; }

    public IReadOnlyCollection<JsonObject> Received => _received;

    public async Task StartAsync()
    {
        await _app.StartAsync();
        Url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    /// <summary>What the real relay's callback page would hand to the browser: a one-time code bound to the pair hash.</summary>
    public string IssueCode(string pairHash)
    {
        var code = Guid.NewGuid().ToString("N");
        _codes[code] = pairHash;
        return code;
    }

    public async Task WaitForConnectionsAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (Connections < count)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {count} connection(s), got {Connections}");
            }

            await _connectedSignal.WaitAsync(TimeSpan.FromMilliseconds(100));
        }
    }

    public async Task<JsonObject> WaitForMessageAsync(string type, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            foreach (var message in _received)
            {
                if ((string?)message["type"] == type)
                {
                    return message;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No '{type}' message received; got: {string.Join(", ", _received.Select(m => (string?)m["type"]))}");
            }

            await _receivedSignal.WaitAsync(TimeSpan.FromMilliseconds(100));
        }
    }

    public async Task PushAsync(object message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, RelayProtocol.Json));
        WebSocket[] targets;
        lock (_sockets)
        {
            targets = _sockets.Where(s => s.State == WebSocketState.Open).ToArray();
        }

        foreach (var socket in targets)
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }

    public async Task CloseAllAsync(int code, string reason)
    {
        WebSocket[] targets;
        lock (_sockets)
        {
            targets = _sockets.Where(s => s.State == WebSocketState.Open).ToArray();
        }

        foreach (var socket in targets)
        {
            try
            {
                await socket.CloseAsync((WebSocketCloseStatus)code, reason, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is InvalidOperationException)
            {
                // already gone
            }
        }
    }

    private async Task PairExchange(HttpContext http)
    {
        var body = await JsonSerializer.DeserializeAsync<JsonObject>(http.Request.Body, RelayProtocol.Json);
        var code = (string?)body?["code"] ?? string.Empty;
        var verifier = (string?)body?["pair_verifier"] ?? string.Empty;
        if (!_codes.TryRemove(code, out var pair))
        {
            http.Response.StatusCode = 404;
            await http.Response.WriteAsJsonAsync(new { error = "invalid_code", detail = "unknown, expired or already used code" });
            return;
        }

        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))) != pair)
        {
            http.Response.StatusCode = 403;
            await http.Response.WriteAsJsonAsync(new { error = "verifier_mismatch", detail = "pair_verifier does not match" });
            return;
        }

        var token = ChannelUserId + "." + Guid.NewGuid().ToString("N");
        _devices[token] = 1;
        await http.Response.WriteAsJsonAsync(new { device_token = token, channel = new { user_id = ChannelUserId, slug = Slug, username = Username } });
    }

    private bool Authorised(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) && _devices.ContainsKey(header[7..]);
    }

    private async Task WebSocketEndpoint(HttpContext http)
    {
        if (RejectSockets || !Authorised(http))
        {
            http.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = 426;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        lock (_sockets)
        {
            _sockets.Add(socket);
            Connections++;
        }

        _connectedSignal.Release();
        var hello = new
        {
            v = 1,
            type = "hello",
            channel = new { user_id = ChannelUserId, slug = Slug, username = Username },
            needs_reconnect = HelloNeedsReconnect,
            missing_scopes = HelloNeedsReconnect ? new[] { "chat:write" } : Array.Empty<string>(),
            live = new { is_live = false, title = (string?)null, started_at = (string?)null, ended_at = (string?)null },
            server_version = "fake-0.1",
        };
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hello, RelayProtocol.Json)), WebSocketMessageType.Text, true, http.RequestAborted);

        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, http.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (text == RelayProtocol.PingFrame)
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"pong\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
                    continue;
                }

                if (JsonNode.Parse(text) is JsonObject message)
                {
                    _received.Enqueue(message);
                    _receivedSignal.Release();
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException || ex is OperationCanceledException)
        {
            // client gone
        }
        finally
        {
            lock (_sockets)
            {
                _sockets.Remove(socket);
            }
        }
    }

    private async Task Disconnect(HttpContext http)
    {
        if (!Authorised(http))
        {
            http.Response.StatusCode = 401;
            return;
        }

        DisconnectCalls++;
        _devices.TryRemove(http.Request.Headers.Authorization.ToString()[7..], out _);
        await CloseAllAsync(4000, "disconnected");
        await http.Response.WriteAsJsonAsync(new { ok = true });
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync(1001, "shutting down");
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
