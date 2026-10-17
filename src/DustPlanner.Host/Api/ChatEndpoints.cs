using DustPlanner.App;
using DustPlanner.Core.Planner;
using DustPlanner.Kick;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DustPlanner.Host.Api;

/// <summary>
/// <c>GET /api/chat/{command}?lang=es|en&amp;user=name</c>: the text a chat bot (Streamer.bot, or anything else on this
/// machine) posts in reply to <c>!command</c>. Plain text, one line, at most 500 characters. <c>404</c> for a command
/// that does not exist or is turned off, <c>204</c> while the command is in cooldown (Streamer.bot's guide says not to
/// post empty replies). Missing data is answered with a friendly line, never with an error.
/// </summary>
internal static class ChatEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/chat/{command}", (string command, string? lang, string? user, AppServices services, OverlayStateService state, ChatResponder responder, TimeProvider clock) =>
        {
            ChatPrefs prefs;
            try
            {
                prefs = services.ReadPrefs().Chat ?? new ChatPrefs();
                prefs.Validate();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException || ex is IOException)
            {
                // A broken prefs.json must not silence the chat: answer with the defaults.
                prefs = new ChatPrefs();
            }

            var reply = responder.Respond(prefs, command, user, lang, ChatContextFactory.From(state.Current), clock.GetUtcNow());
            return reply.Status switch
            {
                ChatReplyStatus.Ok => Results.Text(reply.Text!, "text/plain; charset=utf-8"),
                ChatReplyStatus.Cooldown => Results.NoContent(),
                _ => Results.NotFound(),
            };
        });
    }
}

/// <summary>Maps the overlay state onto what the texts need. The only place the chat learns about the host.</summary>
internal static class ChatContextFactory
{
    public static ChatContext From(OverlayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new ChatContext
        {
            Deck = state.ActiveDeck is { } deck ? new ChatDeck(deck.Name, deck.Deckstring, deck.CardClass, deck.Format) : null,
            DeckUrl = null, // stage K6: the public page's address arrives with the relay's hello
            Rank = state.Rank is { } rank ? new ChatRank(rank.LeagueId, rank.StarLevel, rank.Stars, rank.LegendRank, rank.Format) : null,
            Today = new ChatRecord(state.Today.Wins, state.Today.Losses, state.Today.Ties),
            Session = new ChatRecord(state.Session.Wins, state.Session.Losses, state.Session.Ties),
            Streak = state.Session.Streak,
            LastGame = state.LastGame is { } last ? new ChatLastGame(last.Result, last.PlayerClass, last.OpponentClass, last.Turns) : null,
            Dust = state.Dust?.Value,
        };
    }
}
