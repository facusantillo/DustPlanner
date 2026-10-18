using DustPlanner.Core.Planner;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DustPlanner.Kick;

/// <summary>What the bridge reports to the connector's state after each command (PRD §5.4, shown on the Kick card).</summary>
public static class CommandOutcomes
{
    public const string Answered = "answered";
    public const string Cooldown = "cooldown";
    public const string Ignored = "ignored";
    public const string SendFailed = "send_failed";
}

/// <summary>
/// Stage K5: every <c>chat.command</c> the relay forwards goes through the same <see cref="ChatResponder"/> as the local
/// endpoints — same texts, same cooldown clocks, so a Streamer.bot still polling cannot double an answer within the
/// cooldown — and the text leaves as <c>chat.send</c> replying to the viewer's message. Settings are read per command, so
/// a saved <c>prefs.json</c> applies at once. Knows nothing about the host: it is handed how to read the settings and
/// how to build the <see cref="ChatContext"/>.
/// </summary>
public sealed partial class KickChatBridge : IHostedService
{
    private readonly KickConnector _connector;
    private readonly ChatResponder _responder;
    private readonly Func<ChatPrefs> _prefs;
    private readonly Func<ChatContext> _context;
    private readonly ILogger<KickChatBridge> _log;
    private readonly TimeProvider _clock;

    public KickChatBridge(KickConnector connector, ChatResponder responder, Func<ChatPrefs> prefs, Func<ChatContext> context, ILogger<KickChatBridge> log, TimeProvider? clock = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _connector.CommandReceived += OnCommand;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _connector.CommandReceived -= OnCommand;
        return Task.CompletedTask;
    }

    /// <summary>Decides and sends; exposed for tests and returns the outcome it reported.</summary>
    public async Task<string> HandleAsync(ChatCommandMessage command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        string outcome;
        try
        {
            var prefs = _prefs();
            var reply = _responder.Respond(prefs, command.Command, command.SenderUsername, null, _context(), _clock.GetUtcNow());
            switch (reply.Status)
            {
                case ChatReplyStatus.Ok:
                    var sent = await _connector.SendChatAsync(reply.Text!, string.IsNullOrEmpty(command.MessageId) ? null : command.MessageId, ct).ConfigureAwait(false);
                    outcome = sent ? CommandOutcomes.Answered : CommandOutcomes.SendFailed;
                    break;
                case ChatReplyStatus.Cooldown:
                    outcome = CommandOutcomes.Cooldown;
                    break;
                default:
                    outcome = CommandOutcomes.Ignored;
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken prefs.json or a state hiccup must not take the connection down; the next command gets another chance.
            Log.CommandFailed(_log, command.Command, ex);
            outcome = CommandOutcomes.SendFailed;
        }

        Log.CommandHandled(_log, command.Command, outcome);
        _connector.ReportCommandOutcome(outcome);
        return outcome;
    }

    private void OnCommand(ChatCommandMessage command) => _ = HandleAsync(command);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Kick: !{Command} → {Outcome}")]
        public static partial void CommandHandled(ILogger logger, string command, string outcome);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Kick: answering !{Command} failed")]
        public static partial void CommandFailed(ILogger logger, string command, Exception exception);
    }
}
