namespace Talos.Core.Following;

public enum LogFollowState { Disabled, Watching, WaitingForFile, Faulted }
public enum LogTriggerKind { BoardChanged, RunStarted, RewardShown, EventConfirmed }

/// <summary>Opt-in local following. These are screenshot hints, never a game-state decoder.</summary>
public sealed record LogFollowSettings
{
    public bool Enabled { get; init; }
    public int SettleMilliseconds { get; init; } = 1500;
    public int MaximumDelayMilliseconds { get; init; } = 8000;
    public int ReadBudgetBytes { get; init; } = 262144;
    public int MaxLineCharacters { get; init; } = 8192;
    public int MaxLinesPerPoll { get; init; } = 1000;
    public IReadOnlyList<string> CustomKeywords { get; init; } = Array.Empty<string>();

    public void Validate()
    {
        if (SettleMilliseconds is < 100 or > 10000) throw new ArgumentException("动画等待时间必须为100至10000毫秒。");
        if (MaximumDelayMilliseconds < SettleMilliseconds || MaximumDelayMilliseconds > 60000) throw new ArgumentException("合并等待上限不能短于动画等待，且不能超过60000毫秒。");
        if (ReadBudgetBytes is < 1 or > 1048576) throw new ArgumentException("每次日志读取预算必须为1至1048576字节。");
        if (MaxLineCharacters is < 1 or > 32768) throw new ArgumentException("日志单行长度上限无效。");
        if (MaxLinesPerPoll is < 1 or > 2000) throw new ArgumentException("每次日志行数上限无效。");
        if (CustomKeywords is null || CustomKeywords.Count > 32) throw new ArgumentException("自定义典狱关键字最多32条。");
        foreach (var keyword in CustomKeywords)
            if (!LogLineMatcher.IsValidCustomKeyword(keyword)) throw new ArgumentException("自定义关键字必须是带典狱、牢房、Tartarus或S16标识的具体事件，不能填写普通日志词。");
    }

    internal LogFollowSettings Snapshot()
    {
        Validate();
        return this with { CustomKeywords = CustomKeywords.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }
}

public sealed record LogEventMatch(LogTriggerKind Kind, bool IsRunStartHint);
public sealed record LogTrigger(LogTriggerKind Kind, bool IsRunStartHint, long Generation, DateTimeOffset ObservedAtUtc);
public sealed record LogCoalescingResult(LogTrigger? Trigger, bool PendingExpired);

public sealed record LogFollowStatus
{
    public LogFollowState State { get; init; } = LogFollowState.Disabled;
    public string FileName { get; init; } = "";
    public long Position { get; init; }
    public long TotalBytesRead { get; init; }
    public long TotalLinesRead { get; init; }
    public long TotalMatches { get; init; }
    public bool HasPending { get; init; }
    public long Generation { get; init; }
    /// <summary>A fixed friendly message. No source line or exception message is exposed.</summary>
    public string LastError { get; init; } = "";
}

public sealed record LogFollowPollResult(LogFollowStatus Status, LogTrigger? Trigger = null, bool HasNewMatch = false, bool PendingExpired = false)
{
    /// <summary>A rotation, read gap, missing file or error invalidates advice even without a matched event.</summary>
    public bool RequiresReview { get; init; }
}

/// <summary>Low-level reader output is internal data for matching, not a UI/debug report.</summary>
public sealed record LogTailReadResult(
    IReadOnlyList<string> Lines,
    bool Reset,
    bool WaitingForFile,
    bool SkippedBytes,
    long Position,
    int BytesRead,
    int DroppedLines);
