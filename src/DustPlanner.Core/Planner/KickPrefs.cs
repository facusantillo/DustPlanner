namespace DustPlanner.Core.Planner;

/// <summary>
/// The <c>kick</c> block of <c>prefs.json</c>: what the native Kick connection is allowed to do once connected. Every
/// feature is opt-in and off by default (PRD §6); the connection itself is made from Settings → Kick, not here.
/// </summary>
public sealed class KickPrefs
{
    public const string ReminderOff = "off";
    public const string ReminderPanel = "panel";
    public const string ReminderPanelAndChat = "panel+chat";

    /// <summary>Publish the active deck on <c>live.dustplanner.app/d/&lt;slug&gt;</c> (stage K6).</summary>
    public bool PublicPage { get; set; }

    /// <summary>Post the session record in chat when the stream ends (stage K7).</summary>
    public bool PostStreamSummary { get; set; }

    /// <summary><c>off</c>, <c>panel</c> (private notice) or <c>panel+chat</c>: remind to resolve the prediction when a game ends while live (stage K7).</summary>
    public string PredictionReminder { get; set; } = ReminderPanel;

    /// <summary>Show channel-point redemptions in the overlay (stage K7).</summary>
    public bool ShowRedemptions { get; set; }

    /// <summary>Reward ids to show; empty = every reward.</summary>
    public List<string> RedemptionFilter { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        PredictionReminder = (PredictionReminder ?? ReminderPanel).Trim().ToLowerInvariant();
        if (PredictionReminder is not (ReminderOff or ReminderPanel or ReminderPanelAndChat))
        {
            errors.Add("kick.prediction_reminder must be 'off', 'panel' or 'panel+chat'.");
        }

        RedemptionFilter = (RedemptionFilter ?? new List<string>()).Select(r => (r ?? string.Empty).Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).Take(64).ToList();
        return errors;
    }
}
