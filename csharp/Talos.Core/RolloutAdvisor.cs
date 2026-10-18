using System.Diagnostics;
using Talos.Core.Simulation;

namespace Talos.Core;

public sealed record RolloutOptions
{
    public int Seed { get; init; } = 1;
    public int MaxRounds { get; init; } = 64;
    public int TimeBudgetMs { get; init; } = 1800;
    public int MaxActionsPerGame { get; init; } = 2000;
}

/// <summary>
/// Finite Monte Carlo evaluation of legal first actions, followed by our visible-state
/// policy through day twenty. Every committed round uses one common seed and contains
/// a completed terminal trajectory for every candidate. There is no heuristic fallback.
/// </summary>
public static class RolloutAdvisor
{
    public const string StrategyVersion = "csharp-full-game-rollout-v1";
    private sealed class BudgetExpired : Exception;

    public static AdviceResult Recommend(GameState state, CancellationToken cancellationToken = default)
        => Recommend(state, new RolloutOptions(), cancellationToken);

    public static AdviceResult Recommend(GameState state, RolloutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.MaxRounds is < 1 or > 10000 || options.TimeBudgetMs is < 1 or > 120000 || options.MaxActionsPerGame is < 1 or > 10000)
            throw new ArgumentException("模拟轮数须为1至10000、预算1至120000毫秒、操作上限1至10000。", nameof(options));
        // Copy all caller-owned lists before validation and sampling; input is never mutated.
        ArgumentNullException.ThrowIfNull(state);
        var snapshot = state with
        {
            Rooms = state.Rooms?.ToList()!, Tools = state.Tools?.ToList()!,
            Destinations = state.Destinations?.ToList() ?? [], OfferIds = state.OfferIds?.ToList() ?? []
        };
        var candidates = GameAdvisor.GetCandidates(snapshot);
        if (candidates.Count == 0) throw new InvalidOperationException("当前没有可模拟的合法操作。");
        var watch = Stopwatch.StartNew();
        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.ElapsedMilliseconds >= options.TimeBudgetMs) throw new BudgetExpired();
        }
        var random = new SimulationRandom(options.Seed);
        var values = candidates.Select(_ => new List<double>()).ToArray();
        var seeds = new List<int>();
        var rounds = 0;
        var exhausted = false;
        for (; rounds < options.MaxRounds; rounds++)
        {
            var terminal = new double[candidates.Count];
            var seed = unchecked((int)random.NextUInt());
            try
            {
                for (var index = 0; index < candidates.Count; index++)
                {
                    CheckBudget();
                    var game = SimulationEngine.FromObserved(snapshot, seed);
                    // These actions were enumerated from the same detached observation.
                    game.ApplyLegal(candidates[index]);
                    terminal[index] = game.PlayoutCore(cancellationToken, options.MaxActionsPerGame - 1, CheckBudget);
                    CheckBudget();
                }
            }
            catch (BudgetExpired) { exhausted = true; break; }
            // Commit only full paired rounds. No candidate gets the unfinished round's data.
            for (var index = 0; index < candidates.Count; index++) values[index].Add(terminal[index]);
            seeds.Add(seed);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (rounds == 0) throw new InvalidOperationException("本次预算内没有完成一轮整局配对模拟，请增加预算或切换到明确标注的即时规则模式。");
        var means = values.Select(row => row.Average()).ToArray();
        var order = Enumerable.Range(0, candidates.Count).OrderByDescending(index => means[index]).ThenByDescending(index => candidates[index].Score).ToArray();
        var best = order[0];
        double? Variance(IReadOnlyList<double> row)
        {
            if (row.Count < 2) return null;
            var average = row.Average();
            return row.Sum(value => (value - average) * (value - average)) / (row.Count - 1);
        }
        var ranked = order.Select(index =>
        {
            var variance = Variance(values[index]);
            var differences = values[index].Select((value, n) => values[best][n] - value).ToArray();
            var gapVariance = Variance(differences);
            return candidates[index] with
            {
                Score = means[index], Samples = rounds, SampleVariance = variance,
                StandardError = variance is double v ? Math.Sqrt(v / rounds) : null,
                GapStandardError = gapVariance is double g ? Math.Sqrt(g / rounds) : null,
                Reason = $"{rounds}轮完整配对模拟：先执行此操作，再按可见状态策略走到第20天；分数是终局绮彩总数（含当前{snapshot.Vault}名），不是当前操作成功率。"
            };
        }).ToArray();
        var limitations = "这是近似练习模型内的终局绮彩期望，不是实机概率或全局最优。未来刑具只从本版已支持列表生成，事件等概率，开门/替换分布与初始事件数使用练习假设；多效果按本引擎固定顺序结算。"
            + "后续操作使用当前可见状态启发式，未做本版本整局收益训练验证；不继承旧版本的收益报告。口枷按吞后、升阶前人口≤3，结算时点仍待实机确认；处刑使用基础克制的严格低阶解释。";
        limitations += "副开门后事件标记保留，进入目的地时仍触发；奖励移除当前囚犯后仍保留待处理事件，无囚犯的处刑/暗箱无作用。这些时序作为本地模拟假设。";
        if (rounds < 2) limitations += "仅完成一轮，尚不能估计标准误或置信区间。";
        else limitations += "标准误仅反映模拟抽样波动，不涵盖规则误差；反复比较候选后的区间不构成实机保证。";
        if (exhausted) limitations += "已达本次时间预算，只统计所有候选均完成的配对轮。";
        return new AdviceResult(ranked[0], ranked.Skip(1).ToArray(), StrategyVersion, limitations)
        {
            ScoreKind = "expectedRainbows", SamplesPerAction = rounds,
            Completed = rounds >= options.MaxRounds, Seed = options.Seed,
            ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            StopReason = exhausted ? "timeBudget" : "maxRounds", InitialVault = snapshot.Vault,
            SimulationSeeds = seeds.AsReadOnly()
        };
    }
}
