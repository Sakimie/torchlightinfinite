using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Talos.Core;

namespace Talos.App;

public sealed partial class MainWindow
{
    private async Task RecommendAsync()
    {
        GameState state;
        try
        {
            if (HasFollowPending) throw new InvalidOperationException("日志已提示新的局面变化，请返回游戏等待截图，或先停止日志跟随后手工核对；不能计算旧画面。");
            state = Collect();
            var errors = GameAdvisor.Validate(state);
            if (errors.Count > 0) { status.Text = string.Join("\n", errors); output.Text = status.Text; SetAssistantState("待核对", status.Text); return; }
        }
        catch (Exception error) { status.Text = "无法给出建议：" + error.Message; output.Text = status.Text; SetAssistantState("待核对", status.Text); return; }

        bool simulate = Selected(adviceMode) == "rollout";
        work?.Cancel(); var cancellation = work = new CancellationTokenSource(); var token = cancellation.Token; long epoch = revision;
        SetBusy(true); status.Text = simulate ? "正在本机模拟后续整局，最多64轮 / 约1.8秒预算…" : "正在比较当前一步的即时评分…";
        output.Text = status.Text + "\n本次结果尚未生成，先前建议不再显示。";
        SetAssistantState("计算中", status.Text + "\n局面变化时会取消，本次不会自动操作游戏。");
        try
        {
            var result = await Task.Run(() => simulate
                ? RolloutAdvisor.Recommend(state, new RolloutOptions { MaxRounds = 64, TimeBudgetMs = 1800, MaxActionsPerGame = 2000, Seed = 1 }, token)
                : GameAdvisor.Recommend(state), token);
            if (closed || closing || token.IsCancellationRequested || epoch != revision) return;
            string origin = source == "example" ? "示例研究，非游戏实况" : source == "screenshot" ? "截图经人工核对" : "手工录入";
            bool expected = result.ScoreKind == "expectedRainbows";
            string mode = expected ? "整局模拟 · 有限采样" : "即时评分 · 单步规则";
            string metric = DescribeScore(result.Best, expected);
            string alternatives = string.Join("\n", result.Alternatives.Take(3).Select(candidate => $"备选：{candidate.Label} · {DescribeScore(candidate, expected)}"));
            string uncertainty = expected
                ? $"每动作完成 {result.SamplesPerAction} 轮；{(result.Completed ? "达到轮数上限" : "预算结束，仅计完整配对轮")}，耗时 {result.ElapsedMilliseconds / 1000d:F2} 秒。标准误只反映模拟采样波动，不是识图置信或实机概率。"
                : "即时相对分不代表整局收益或概率。";
            output.Text = $"推荐：{result.Best.Label}\n{result.Best.Reason}\n{metric}\n来源：{origin} · {mode}\n{alternatives}\n{uncertainty}\n{result.Limitations}";
            SetAssistantState("建议已生成", $"{origin}\n{result.Best.Label}\n{result.Best.Reason}\n{mode}\n{metric}");
            status.Text = expected ? $"完成 {result.SamplesPerAction} 轮 / 动作。有限采样不保证最优，局面变化后重算。" : "即时建议已生成；只比较当前一步，局面变化后重算。";
        }
        catch (OperationCanceledException) { if (!closed && !closing && epoch == revision) SetAssistantState("已取消", "本次计算已取消，请重新核对局面。"); }
        catch (Exception error)
        {
            if (!closed && !closing && epoch == revision) { status.Text = "计算未完成：" + error.Message + (simulate ? " 可手动切换即时评分；不会自动回退模式。" : ""); output.Text = status.Text; SetAssistantState("计算未完成", status.Text); }
        }
        finally { CompleteWork(cancellation); }
    }

    private static string DescribeScore(AdviceCandidate candidate, bool expected)
    {
        if (!expected) return $"即时相对分 {candidate.Score:F2}";
        string standardError = candidate.StandardError is { } value ? $"标准误 {value:F3}" : "样本不足以估计标准误";
        return $"终局绮彩总数均值 {candidate.Score:F2}（含已有） · {candidate.Samples} 样本 · {standardError}";
    }
}
