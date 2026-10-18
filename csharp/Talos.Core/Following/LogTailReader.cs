using System.Text;

namespace Talos.Core.Following;

/// <summary>
/// Reads one selected local log, shared read/write/delete, without creating or modifying it.
/// Each poll opens the path afresh so replacement does not pin an obsolete file handle.
/// Content reads are budgeted, with at most 640 extra identity/anchor bytes per poll.
/// </summary>
public sealed class LogTailReader : IDisposable, IAsyncDisposable
{
    private readonly string path;
    private readonly LogFollowSettings settings;
    private readonly StringBuilder partial = new();
    private Decoder decoder = Encoding.UTF8.GetDecoder();
    private byte[] head = [], anchor = [];
    private long position;
    private bool started, initialized, disposed, discardLine, discardLineReported, tailOnRecovery;

    public LogTailReader(string path, LogFollowSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(settings);
        if (!string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择一个.log日志文件。");
        this.path = Path.GetFullPath(path); this.settings = settings.Snapshot();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        started = true; initialized = false; tailOnRecovery = true; position = 0; ResetDecoder(); head = anchor = [];
        try
        {
            using var stream = Open();
            position = stream.Length;
            head = ReadAt(stream, 0, (int)Math.Min(256, position));
            anchor = ReadAt(stream, Math.Max(0, position - 64), (int)Math.Min(64, position));
            // Existing partial text belongs to the pre-connection log; its continuation is ignored.
            discardLine = position > 0 && anchor.Length > 0 && anchor[^1] != (byte)'\n';
            initialized = true; tailOnRecovery = false;
        }
        catch (FileNotFoundException) { tailOnRecovery = false; }
        catch (DirectoryNotFoundException) { tailOnRecovery = false; }
    }

    public async Task<LogTailReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started) throw new InvalidOperationException("请先启动日志追读。");
        cancellationToken.ThrowIfCancellationRequested();
        FileStream stream;
        try { stream = Open(); }
        catch (FileNotFoundException) { return Missing(); }
        catch (DirectoryNotFoundException) { return Missing(); }
        await using (stream)
        {
            long length = stream.Length;
            if (!initialized && tailOnRecovery)
            {
                // A temporarily locked existing file is still old history when access returns.
                // Only a file explicitly absent at connection time is read from its beginning.
                position = length; ResetDecoder();
                head = await ReadAtAsync(stream, 0, (int)Math.Min(256, position), cancellationToken).ConfigureAwait(false);
                anchor = await ReadAtAsync(stream, Math.Max(0, position - 64), (int)Math.Min(64, position), cancellationToken).ConfigureAwait(false);
                discardLine = position > 0 && anchor.Length > 0 && anchor[^1] != (byte)'\n';
                initialized = true; tailOnRecovery = false;
                return new(Array.Empty<string>(), true, false, false, position, 0, 0);
            }
            bool reset = !initialized;
            if (initialized)
            {
                // CreationTime can reflect ctime on some filesystems and change on append.
                // Content anchors plus length detect meaningful replacement without replaying
                // ordinary appends or resetting an incomplete UTF8 character.
                reset = length < position;
                if (!reset && head.Length > 0)
                    reset = !(await ReadAtAsync(stream, 0, head.Length, cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(head);
                if (!reset && anchor.Length > 0)
                    reset = !(await ReadAtAsync(stream, position - anchor.Length, anchor.Length, cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(anchor);
            }
            if (reset) { position = 0; ResetDecoder(); }
            initialized = true;
            bool skipped = length - position > settings.ReadBudgetBytes;
            if (skipped) { position = length - settings.ReadBudgetBytes; ResetDecoder(); discardLine = true; }
            var lines = new Queue<string>(); int bytesRead = 0, dropped = 0;
            int remaining = (int)Math.Min(settings.ReadBudgetBytes, Math.Max(0, length - position));
            var bytes = new byte[Math.Min(16384, Math.Max(1, remaining))];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            stream.Position = position;
            while (remaining > 0)
            {
                int count = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                position += count; bytesRead += count; remaining -= count;
                int charCount = decoder.GetChars(bytes, 0, count, chars, 0, flush: false);
                for (int index = 0; index < charCount; index++)
                {
                    char character = chars[index];
                    if (character == '\n')
                    {
                        if (discardLine) { if (!discardLineReported) dropped++; }
                        else
                        {
                            if (partial.Length > 0 && partial[^1] == '\r') partial.Length--;
                            if (lines.Count == settings.MaxLinesPerPoll) { lines.Dequeue(); dropped++; }
                            lines.Enqueue(partial.ToString());
                        }
                        partial.Clear(); discardLine = discardLineReported = false;
                    }
                    else if (!discardLine)
                    {
                        if (partial.Length >= settings.MaxLineCharacters) { partial.Clear(); discardLine = discardLineReported = true; dropped++; }
                        else partial.Append(character);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            head = await ReadAtAsync(stream, 0, (int)Math.Min(256, position), cancellationToken).ConfigureAwait(false);
            anchor = await ReadAtAsync(stream, Math.Max(0, position - 64), (int)Math.Min(64, position), cancellationToken).ConfigureAwait(false);
            return new(lines.ToArray(), reset, false, skipped, position, bytesRead, dropped);
        }
    }

    private LogTailReadResult Missing()
    {
        initialized = false; position = 0; head = anchor = []; ResetDecoder();
        return new(Array.Empty<string>(), true, true, false, 0, 0, 0);
    }
    private FileStream Open() => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private void ResetDecoder() { decoder = Encoding.UTF8.GetDecoder(); partial.Clear(); discardLine = discardLineReported = false; }
    private static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        var result = new byte[count]; stream.Position = offset;
        int read = 0, got;
        while (read < count && (got = stream.Read(result, read, count - read)) > 0) read += got;
        return read == count ? result : result[..read];
    }
    private static async Task<byte[]> ReadAtAsync(FileStream stream, long offset, int count, CancellationToken token)
    {
        var result = new byte[count]; stream.Position = offset;
        int read = 0, got;
        while (read < count && (got = await stream.ReadAsync(result.AsMemory(read, count - read), token).ConfigureAwait(false)) > 0) read += got;
        return read == count ? result : result[..read];
    }
    public void Dispose() { disposed = true; ResetDecoder(); }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
