namespace DustPlanner.Host;

/// <summary>A one-off event for the panel, the status page and the overlay, streamed on <c>/events</c> as <c>event: kick.&lt;type&gt;</c>.</summary>
public sealed record KickNotice(string Type, object Payload, DateTimeOffset At)
{
    public const string Reminder = "reminder";
    public const string Redemption = "redemption";
}

/// <summary>In-process fan-out of <see cref="KickNotice"/>s. Nothing is stored: a notice that nobody is listening to is gone.</summary>
public sealed class KickNotices
{
    public event Action<KickNotice>? Published;

    public void Publish(KickNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Published?.Invoke(notice);
    }
}
