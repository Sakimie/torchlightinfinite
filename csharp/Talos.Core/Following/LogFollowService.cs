using System.Collections.Concurrent;

namespace Talos.Core.Following;

/// <summary>
/// One optional follower. Poll from a UI timer, apply its result on the UI thread,
/// and invalidate advice immediately on HasNewMatch, before waiting for Trigger.
/// This service performs no screenshots, game input, uploads, or background work of its own.
/// </summary>
public sealed class LogFollowService : IAsyncDisposable
{
    private sealed class Session
    {
        public required LogTailReader Reader;
        public required LogLineMatcher Matcher;
        public required LogTriggerCoalescer Coalescer;
        public CancellationTokenSource Cancellation { get; } = new();
        public long Bytes, Lines, Matches;
        public string FileName = "";
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object lifecycle = new();
    private readonly Func<DateTimeOffset> clock;
    private Session? session;
    private LogFollowStatus status = new();
    private bool disposed;
    public LogFollowStatus Status => status;

    public LogFollowService(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    public void Start(string path, LogFollowSettings settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.Snapshot();
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Stop();
            if (!snapshot.Enabled) return;
            var next = new Session { Reader = new LogTailReader(path, snapshot), Matcher = new LogLineMatcher(snapshot), Coalescer = new LogTriggerCoalescer(snapshot), FileName = Path.GetFileName(path) };
            session = next;
            try { next.Reader.Start(); status = new() { State = LogFollowState.Watching, FileName = next.FileName }; }
            catch (Exception error) when (IsReadError(error)) { status = new() { State = LogFollowState.Faulted, FileName = next.FileName, LastError = Friendly(error) }; }
        }
    }

    public async Task<LogFollowPollResult> PollAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = session;
            if (active is null) return new(status);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, active.Cancellation.Token);
            try
            {
                var read = await active.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                if (!ReferenceEquals(active, session)) return new(status);
                if (read.Reset || read.WaitingForFile || read.SkippedBytes || read.DroppedLines > 0) active.Coalescer.Reset();
                active.Bytes += read.BytesRead; active.Lines += read.Lines.Count;
                bool matched = false; var now = clock();
                foreach (var line in read.Lines)
                {
                    var match = active.Matcher.Match(line);
                    if (match is null) continue;
                    matched = true; active.Matches++; active.Coalescer.Observe(match, now);
                }
                var outcome = read.WaitingForFile ? new LogCoalescingResult(null, false) : active.Coalescer.Advance(now);
                string note = outcome.PendingExpired ? "典狱事件连续变化，已放弃本次自动截图；等待下一次稳定事件。"
                    : read.SkippedBytes || read.DroppedLines > 0 ? "日志增长过快或单行过长，已跳过部分内容；请核对画面，必要时手动截图。" : "";
                lock (lifecycle)
                {
                    if (!ReferenceEquals(active, session)) return new(status);
                    status = new()
                    {
                        State = read.WaitingForFile ? LogFollowState.WaitingForFile : LogFollowState.Watching,
                        FileName = active.FileName, Position = read.Position, TotalBytesRead = active.Bytes, TotalLinesRead = active.Lines,
                        TotalMatches = active.Matches, HasPending = active.Coalescer.HasPending, Generation = active.Coalescer.Generation, LastError = note
                    };
                    return new(status, outcome.Trigger, matched, outcome.PendingExpired)
                    { RequiresReview = read.Reset || read.WaitingForFile || read.SkippedBytes || read.DroppedLines > 0 || outcome.PendingExpired };
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && active.Cancellation.IsCancellationRequested) { return new(status); }
            catch (Exception error) when (IsReadError(error))
            {
                lock (lifecycle)
                {
                    if (!ReferenceEquals(active, session)) return new(status);
                    active.Coalescer.Reset();
                    status = status with { State = LogFollowState.Faulted, HasPending = false, LastError = Friendly(error) };
                    return new(status) { RequiresReview = true };
                }
            }
        }
        finally { CleanupRetired(); gate.Release(); }
    }

    public void Stop()
    {
        lock (lifecycle)
        {
            var old = Interlocked.Exchange(ref session, null);
            old?.Cancellation.Cancel();
            status = new();
            if (old is not null) retired.Enqueue(old);
        }
        // Clean immediately if idle, otherwise the in-flight poll cleans after its read exits.
        if (gate.Wait(0)) { try { CleanupRetired(); } finally { gate.Release(); } }
    }
    private readonly ConcurrentQueue<Session> retired = new();
    private void CleanupRetired()
    {
        while (retired.TryDequeue(out var old)) { old.Cancellation.Dispose(); old.Reader.Dispose(); }
    }
    private static bool IsReadError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static string Friendly(Exception error) => error is UnauthorizedAccessException or System.Security.SecurityException
        ? "没有读取所选日志的权限，请重新选择可读的游戏日志。" : "暂时无法读取所选日志，将在下一次轮询重试。";

    public async ValueTask DisposeAsync()
    {
        lock (lifecycle) { if (disposed) return; disposed = true; Stop(); }
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CleanupRetired();
        }
        finally { gate.Release(); }
    }
}
