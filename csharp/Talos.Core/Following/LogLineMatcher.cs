namespace Talos.Core.Following;

/// <summary>
/// Independently written event grammar: a prison domain and a specific action must coexist.
/// Built-in labels are conservative candidates, not verified country/client log signatures.
/// Exact custom phrases can adapt a user's locally observed event without matching ordinary logs.
/// </summary>
public sealed class LogLineMatcher
{
    private readonly string[] custom;

    public LogLineMatcher(LogFollowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        custom = settings.Snapshot().CustomKeywords.ToArray();
    }

    public LogEventMatch? Match(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !HasPrisonDomain(line)) return null;
        if (HasWord(line, "start") || HasWord(line, "started") || HasAny(line, "挑战开始", "开始挑战", "新局开始"))
            return new(LogTriggerKind.RunStarted, true);
        if ((HasWord(line, "reward") || HasWord(line, "treasure") || HasAny(line, "奖励", "珍藏", "刑具")) &&
            (HasWord(line, "shown") || HasWord(line, "opened") || HasWord(line, "choose") || HasAny(line, "弹窗", "出现", "待选")))
            return new(LogTriggerKind.RewardShown, false);
        if ((HasWord(line, "event") || line.Contains("事件", StringComparison.Ordinal)) &&
            (HasWord(line, "confirm") || HasWord(line, "confirmed") || line.Contains("确认", StringComparison.Ordinal)))
            return new(LogTriggerKind.EventConfirmed, false);
        if (new[] { "devour", "consume", "eat", "swap", "move", "moved" }.Any(word => HasWord(line, word)) ||
            HasAny(line, "吞噬完成", "替换完成", "移动完成", "行动完成"))
            return new(LogTriggerKind.BoardChanged, false);
        return custom.Any(keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            ? new(LogTriggerKind.BoardChanged, false) : null;
    }

    public static bool IsValidCustomKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return false;
        var value = keyword.Trim();
        if (value.Length is < 4 or > 160 || value.Any(char.IsControl) || !HasPrisonDomain(value)) return false;
        var rest = value;
        foreach (var domain in new[] { "典狱", "监狱", "牢房", "审讯室", "tartarus", "s16board", "s16gameplay", "s16" })
            rest = rest.Replace(domain, "", StringComparison.OrdinalIgnoreCase);
        // A domain alone, a wildcard, or just punctuation cannot become an all-lines trigger.
        if (rest.Count(char.IsLetterOrDigit) < 2) return false;
        var words = new string(rest.Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word.Any(char.IsLetter)).ToArray();
        var ordinary = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "log", "info", "debug", "warn", "warning", "error", "trace", "board", "game", "gameplay", "ui", "btn", "event", "message", "日志", "错误", "警告", "信息", "事件" };
        return words.Any(word => !ordinary.Contains(word));
    }

    private static bool HasPrisonDomain(string line)
    {
        if (HasAny(line, "典狱", "监狱", "牢房", "审讯室") || HasWord(line, "tartarus")) return true;
        int offset = 0;
        while ((offset = line.IndexOf("s16", offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = offset + 3;
            if ((offset == 0 || !char.IsLetterOrDigit(line[offset - 1])) && (end == line.Length || !char.IsDigit(line[end]))) return true;
            offset = end;
        }
        return false;
    }

    private static bool HasAny(string line, params string[] phrases) => phrases.Any(phrase => line.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    private static bool HasWord(string line, string word)
    {
        int offset = 0;
        while ((offset = line.IndexOf(word, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = offset + word.Length;
            if ((offset == 0 || !char.IsLetterOrDigit(line[offset - 1])) && (end == line.Length || !char.IsLetterOrDigit(line[end]))) return true;
            offset = end;
        }
        return false;
    }
}
