namespace Talos.Core.Following;

/// <summary>Debounces explicit events. An endless burst expires instead of forcing an unstable screenshot.</summary>
public sealed class LogTriggerCoalescer
{
    private readonly TimeSpan settle, maximum;
    private DateTimeOffset first, last;
    private LogTriggerKind kind;
    private bool runStart;
    public bool HasPending { get; private set; }
    public long Generation { get; private set; }

    public LogTriggerCoalescer(LogFollowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate();
        settle = TimeSpan.FromMilliseconds(settings.SettleMilliseconds);
        maximum = TimeSpan.FromMilliseconds(settings.MaximumDelayMilliseconds);
    }

    public void Observe(LogEventMatch match, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (!HasPending) { first = last = now; kind = match.Kind; runStart = false; HasPending = true; }
        else if (now > last) last = now;
        if ((int)match.Kind > (int)kind) kind = match.Kind;
        runStart |= match.IsRunStartHint;
        Generation++;
    }

    public LogCoalescingResult Advance(DateTimeOffset now)
    {
        if (!HasPending) return new(null, false);
        if (now - last >= settle)
        {
            var trigger = new LogTrigger(runStart ? LogTriggerKind.RunStarted : kind, runStart, Generation, last);
            HasPending = false;
            return new(trigger, false);
        }
        if (now - first >= maximum) { HasPending = false; return new(null, true); }
        return new(null, false);
    }

    public void Reset() { HasPending = false; first = last = default; runStart = false; }
}
