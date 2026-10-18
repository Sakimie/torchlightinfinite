namespace Talos.Core;

/// <summary>
/// Original visible-information heuristics, ported from this project's own policy.
/// No simulator process, external policy weights, network, or hidden-future state is used.
/// </summary>
public static class GameAdvisor
{
    private const double WOpen = 1.5, WNow = 1, WWalk = .65, WStep = .25,
        WInstigate = .6, WCrow = 1, WChain = 1, WStair = .2, RainbowMult = 2, TierBase = 2;
    private const int SwapMaxTier = 2;

    public static IReadOnlyList<string> Validate(GameState state)
    {
        var errors = new List<string>();
        if (state is null) return ["缺少棋盘数据。"];
        if (!state.Confirmed) errors.Add("请人工核对截图、天数、位置、刑具和阶段后确认棋盘。");
        if (state.UnsupportedPresent) errors.Add("存在尚未支持的刑具、标记、额外行动或特殊开局，不能省略后推荐。");
        if (state.Day is < 1 or > 20) errors.Add("当前天数必须为1至20；本版只支持普通20天。");
        if (state.Position is < 0 or > 15) errors.Add("典狱长位置必须为0至15。");
        if (state.Vault is < 0 or > 1000000) errors.Add("已获绮彩数量必须为0至1000000。");
        if (state.Objective is null || !EngineCatalog.Objectives.ContainsKey(state.Objective)) errors.Add("请确认当前任务。");
        if (state.ObjectiveProgress is < 0 or > 99) errors.Add("任务进度必须为0至99。");
        if (state.UsurpStage is < 1 or > 5) errors.Add("请确认僭越进度：1至4，或5表示已结束。");
        if (state.Phase is null || !EngineCatalog.Phases.ContainsKey(state.Phase)) errors.Add("请确认当前决策阶段。");
        if (state.Tools is null) errors.Add("请明确确认持有刑具列表；没有刑具时可为空。");
        else
        {
            if (state.Tools.Count > EngineCatalog.Tools.Count || state.Tools.Distinct(StringComparer.Ordinal).Count() != state.Tools.Count) errors.Add("刑具列表过长或重复。");
            foreach (var id in state.Tools)
                if (id is null || !EngineCatalog.Tools.ContainsKey(id)) errors.Add($"刑具尚未支持或未识别：{id ?? "空值"}。");
        }
        var roomsValid = state.Rooms is not null && state.Rooms.Count == 16 && state.Rooms.All(room => room is not null);
        if (!roomsValid) errors.Add("需要审讯室及15间牢房，不能缺格。");
        else
        {
            for (var n = 0; n < state.Rooms!.Count; n++)
            {
                var room = state.Rooms[n];
                if (room.Index != n) errors.Add("牢房编号必须从0至15连续且不可重复。");
                if (room.Kind is null || !EngineCatalog.Kinds.ContainsKey(room.Kind) || room.Kind == "unknown") errors.Add($"请确认{n + 1}号牢房状态；未确认格不能当作未开门。");
                if (n == 0)
                {
                    if (room.Kind != "office" || room.Species is not null || room.Tier is not null || room.Event || room.EventKind is not null || room.Ivory || room.NestDue is not null || room.KnownTier is not null || room.KnownSpecies is not null || room.KnownMinimumTier is not null) errors.Add("1号位置必须为没有囚犯和标记的审讯室。");
                    continue;
                }
                if (room.Kind == "office") errors.Add("只有1号位置可以是审讯室。");
                if (room.Kind == "inmate")
                {
                    if (room.Species is null || !EngineCatalog.Species.ContainsKey(room.Species)) errors.Add($"请确认{n + 1}号囚犯种族。");
                    if (room.Tier == 5) errors.Add("绮彩囚犯已经离场入库，请修改已获数量，并把原房间改为未开门。");
                    else if (room.Tier is null or < 0 or > 4) errors.Add($"请确认{n + 1}号囚犯稀有度（0至4）。");
                }
                else if (room.Species is not null || room.Tier is not null || room.Ivory) errors.Add($"{n + 1}号非囚犯牢房不应带可见囚犯或象牙信息。");
                if (room.Kind == "nest" && (room.NestDue is null || room.NestDue <= state.Day || room.NestDue > 30)) errors.Add($"请确认{n + 1}号鼠巢未来的孵化日。");
                if (room.Kind != "nest" && room.NestDue is not null) errors.Add($"{n + 1}号不是鼠巢，不能保留孵化日期。");
                if (room.KnownTier is not null && (room.Kind != "locked" || room.KnownTier is < 0 or > 4)) errors.Add("已探知隐藏稀有度只能用于未开门牢房，且须为0至4。");
                if (room.KnownSpecies is not null && (room.KnownTier is null || !EngineCatalog.Species.ContainsKey(room.KnownSpecies))) errors.Add("已探知隐藏种族必须有效，并同时确认稀有度。");
                if (room.KnownMinimumTier is not null && (room.Kind != "locked" || room.KnownMinimumTier is < 0 or > 4 || room.KnownTier < room.KnownMinimumTier)) errors.Add("已探知阶级下界只能用于未开门，须为0至4且不高于已确认阶级。");
                if (room.EventKind is not null && !EngineCatalog.Events.ContainsKey(room.EventKind)) errors.Add($"{n + 1}号存在未支持的事件。");
                if (room.EventKind is not null && !room.Event) errors.Add($"{n + 1}号事件类型需要同时确认事件标记。");
                if (room.Event && room.Kind is not ("locked" or "nest" or "inmate")) errors.Add("未触发事件标记只能放在未开门、鼠巢或有囚犯的房间；当前事件仍需在阶段中选择。");
            }
        }
        if (state.EventKind is not null && !EngineCatalog.Events.ContainsKey(state.EventKind)) errors.Add("当前事件尚未支持，不能忽略其效果。");
        var pending = state.Phase is "tool" or "gift";
        var phase = pending ? state.ResumePhase : state.Phase;
        if (pending)
        {
            if (phase is not ("patrol" or "manage" or "office" or "event" or "chain" or "ratway")) errors.Add("请确认选完奖励后回到哪个阶段。");
            var choices = state.OfferIds;
            var catalog = state.Phase == "gift" ? EngineCatalog.Gifts : EngineCatalog.Tools;
            if (choices is null || choices.Count == 0 || choices.Count > (state.Phase == "gift" ? 4 : 3) || choices.Distinct(StringComparer.Ordinal).Count() != choices.Count) errors.Add("请明确录入当前实际可选奖励，不能默认补全。");
            else foreach (var id in choices)
                if (id is null || !catalog.ContainsKey(id) || state.Phase == "tool" && state.Tools?.Contains(id) == true) errors.Add("待选奖励无效、未支持或包含已持有刑具。");
        }
        if (phase == "patrol")
        {
            if (state.Destinations is null || state.Destinations.Count is < 1 or > 6 || state.Destinations.Distinct().Count() != state.Destinations.Count) errors.Add("请明确录入今天实际可去的牢房。");
            else if (state.Destinations.Any(n => n is < 0 or > 15 || n == state.Position || GameRules.StepsBetween(state.Position, n) > 6)) errors.Add("普通巡逻目的地必须在前方六格内。");
        }
        if (phase == "office" && state.Position != 0) errors.Add("煽动或赏赐需要典狱长在审讯室。");
        if (phase == "event")
        {
            if (state.EventKind is null || !EngineCatalog.Events.ContainsKey(state.EventKind)) errors.Add("请确认当前事件类型。");
            if (state.Position == 0) errors.Add("当前事件需要位于非审讯室房间；奖励可能已使原囚犯离场。");
            if (state.EventKind?.StartsWith("usurp", StringComparison.Ordinal) == true && int.TryParse(state.EventKind.AsSpan(5), out var stage) && stage != state.UsurpStage) errors.Add("当前僭越事件与已确认进度不一致。");
        }
        if (phase == "chain")
        {
            if (state.ChainConsumed is < 1 or > 20) errors.Add("请确认本次连吃已吞口数，至少为1。");
            if (roomsValid && state.Position is >= 0 and <= 15)
            {
                var eater = state.Rooms![state.Position];
                if (!GameRules.IsInmate(eater) || !state.Rooms.Any(room => GameRules.CanEat(state, eater, room))) errors.Add("当前没有合法连吃目标，请核对阶段和囚犯。");
                if (GameRules.IsInmate(eater) && state.ChainConsumed > eater.Tier) errors.Add("本次已吞口数与当前囚犯阶级不一致，请核对连吃记录。");
            }
        }
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static GameState CreateEmpty() => new() { UsurpStage = 0, Confirmed = false, Source = "manual" };
    public static GameState CreateExample()
    {
        var rooms = Enumerable.Range(0, 16).Select(n => new RoomState { Index = n, Kind = n == 0 ? "office" : "locked" }).ToList();
        rooms[2] = new RoomState { Index = 2, Kind = "inmate", Species = "lion", Tier = 3 };
        rooms[4] = new RoomState { Index = 4, Kind = "inmate", Species = "wolf", Tier = 3 };
        rooms[7] = new RoomState { Index = 7, Kind = "inmate", Species = "rat", Tier = 4 };
        rooms[9] = new RoomState { Index = 9, Kind = "inmate", Species = "mammoth", Tier = 2 };
        rooms[12] = new RoomState { Index = 12, Kind = "inmate", Species = "lion", Tier = 2 };
        return new GameState { Day = 12, Position = 2, Phase = "manage", Rooms = rooms, Tools = ["collar", "revenge"], Vault = 1, Objective = "king", Milestone = true, Confirmed = true, Source = "example" };
    }

    public static AdviceResult Recommend(GameState state)
    {
        var ranked = GetCandidates(state);
        if (ranked.Count == 0) throw new InvalidOperationException("当前没有合法操作，请核对阶段。");
        var limitations = "相对启发式评分，不是概率或绝对最优；未知门采用近似分布，C#版本未做整局训练验证。";
        if (GameRules.HasTool(state, "muzzle")) limitations += EngineCatalog.RuleAssumptions[0];
        if (state.Phase == "event" && state.EventKind == "execution") limitations += "处刑按基础克制的低阶目标估值，扩展克制解释尚待实机确认。";
        if (state.Objective is "consume" or "other") limitations += "当前任务奖励存在未验证或未建模条件。";
        return new AdviceResult(ranked[0], ranked.Skip(1).ToArray(), EngineCatalog.StrategyVersion, limitations);
    }

    public static IReadOnlyList<AdviceCandidate> GetCandidates(GameState state)
    {
        var errors = Validate(state);
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors), nameof(state));
        // Snapshot lists so a concurrent editor cannot alter the evaluated observation.
        var snapshot = state with { Rooms = state.Rooms.ToList(), Tools = state.Tools.ToList(), Destinations = state.Destinations?.ToList() ?? [], OfferIds = state.OfferIds?.ToList() ?? [] };
        var evaluator = new Evaluation(snapshot);
        return evaluator.Actions().OrderByDescending(a => a.Score).ToArray();
    }

    private sealed class Evaluation(GameState state)
    {
        private readonly GameState _st = state;
        private List<RoomState> Inmates => _st.Rooms.Where(GameRules.IsInmate).ToList();
        private List<RoomState> Doors => _st.Rooms.Where(c => c.Kind == "locked").ToList();
        private bool Has(string id) => GameRules.HasTool(_st, id);
        private int Days => Math.Max(0, 21 - _st.Day);
        private int Sealed => _st.Rooms.Count(c => c.Kind is "locked" or "nest");
        private double Tier(int tier)
        {
            if (tier >= 5) return RainbowMult;
            var value = RainbowMult / Math.Pow(TierBase, 5 - tier + .3);
            return value * Math.Min(1, (Days + 1) / Math.Max(2, (5 - tier) * 1.3));
        }
        private double Lift(int tier, int by) => Tier(tier + by) - Tier(tier);
        private double Expected(int minimum = 0, bool replacement = false)
        {
            var weights = replacement ? EngineCatalog.ReplacementWeights : EngineCatalog.OpenWeights;
            var total = weights.Skip(minimum).Sum();
            return total == 0 ? Tier(minimum) : weights.Skip(minimum).Select((weight, n) => weight * Tier(n + minimum)).Sum() / total;
        }
        private List<RoomState> Meals(RoomState eater) => Inmates.Where(c => GameRules.CanEat(_st, eater, c)).ToList();
        private double AverageLift(int by) => Inmates.Count == 0 ? 0 : Inmates.Average(c => Lift(c.Tier!.Value, by));
        private double EventPrior() => EventPool().Average(id => Math.Max(0, EventWorth(id)));
        private IReadOnlyList<string> EventPool()
        {
            var pool = EngineCatalog.Events.Keys.Where(id => !id.StartsWith("usurp", StringComparison.Ordinal)).ToList();
            if (_st.UsurpStage <= 4) pool.Add($"usurp{_st.UsurpStage}");
            return pool;
        }
        private double EventWorth(string id)
        {
            if (id.StartsWith("usurp", StringComparison.Ordinal))
            {
                var (count, by) = GameRules.EventPromotion(id);
                var stair = id == "usurp4" ? 0 : Math.Min(.65, Days / 30d) * WStair;
                return Math.Min(count, Inmates.Count) * AverageLift(by) + stair;
            }
            var here = _st.Rooms[_st.Position];
            return id switch
            {
                "freedom" => Math.Min(3, Doors.Count) * Expected(Has("hook") ? 1 : 0) * WOpen,
                "convict" => Doors.Count > 0 ? Expected(3) * WOpen : 0,
                "chain" => Math.Min(Doors.Count, 3) * Expected() * Math.Min(2, WChain),
                "trickster" => GameRules.IsInmate(here) ? Lift(here.Tier!.Value, Math.Max(0, 4 - here.Tier.Value)) : 0,
                "appetite" => AverageLift(1) * 2.5,
                "execution" => ExecutionWorth(here),
                "crow" => Math.Min(4, Doors.Count) * .02 * WCrow,
                "hunt" => Doors.Count * .006,
                "ratway" => .04 + Inmates.Select(c => Meals(c).Count).DefaultIfEmpty(0).Max() * .01,
                _ => 0
            };
        }
        private double ExecutionWorth(RoomState here)
        {
            if (!GameRules.IsInmate(here)) return 0;
            var targets = GameRules.ExecutionTargets(_st, here);
            return Lift(here.Tier!.Value, targets.Count) - targets.Sum(c => Tier(c.Tier!.Value));
        }
        private double Devour(RoomState eater, RoomState meal, bool chain)
        {
            var tier = eater.Tier!.Value;
            var by = GameRules.GuaranteedDevourGain(_st, eater, meal);
            double after(int gain) => Lift(tier, gain);
            // Independent Saw and Whip rolls combine before the Rainbow cap.
            var saw = Has("saw") ? .25 : 0;
            var whip = Has("whip") ? .35 : 0;
            double RandomLift(int gain) => (1 - saw) * (1 - whip) * after(gain)
                + (saw + whip - 2 * saw * whip) * after(gain + 1) + saw * whip * after(gain + 2);
            var gain = Has("collar") && chain ? .2 * RandomLift(by) + .8 * RandomLift(by + 1) : RandomLift(by);
            var score = gain - Tier(meal.Tier!.Value);
            if (Has("cage")) score += .4 * Expected(Has("hook") ? 1 : 0) * WOpen;
            if (tier + by < 5)
            {
                var follow = EngineCatalog.Species.Keys.Average(species => Inmates.Count(c => c.Index != eater.Index && c.Index != meal.Index && GameRules.CanEat(_st, eater with { Species = species, Tier = tier + by }, c)));
                score += Math.Min(1, follow) * Lift(tier + by, 1) * .55;
                if (Has("revenge") && Doors.Count > 0) score += .3 * Lift(tier + by, 1) * .7;
            }
            if (Has("coin") && Inmates.Count(c => c.Species == meal.Species && c.Tier == meal.Tier) == 2) score -= Math.Min(.15, Days * .015);
            if (meal.Ivory && Has("tongs")) score -= .06;
            return score * WNow;
        }
        private double Swap(RoomState current)
        {
            var tier = current.Tier!.Value;
            if (tier > SwapMaxTier && !(current.Ivory && Has("tongs")) && !Has("scapegoat") && !Has("maiden")) return -100;
            var others = Inmates.Where(c => c.Index != current.Index).ToList();
            var minimum = Has("rope") ? 2 : 0;
            var cost = Tier(tier);
            if (Has("scapegoat") && others.Count >= 2)
            {
                minimum = Math.Min(5, Math.Max(minimum, tier + 1));
                cost += 2 * others.Average(c => Tier(c.Tier!.Value));
            }
            var gain = minimum >= 5 ? Tier(5) : Expected(minimum, true);
            if (Has("stake") && tier < 3) gain = .6 * gain + .4 * Math.Max(gain, Tier(3));
            if (Has("maiden") && minimum < 5)
            {
                var weights = EngineCatalog.ReplacementWeights;
                var total = weights.Skip(minimum).Sum();
                for (var t = minimum; t <= 4; t++)
                    if (others.Count(c => c.Tier > t) == others.Count(c => c.Tier < t)) gain += weights[t] / total * Lift(t, 3);
            }
            if (current.Ivory && Has("tongs")) gain += Math.Min(4, Inmates.Count) * AverageLift(1);
            return (gain - cost) * WNow;
        }
        private double Tool(string id)
        {
            var meanGain = AverageLift(1);
            var paired = Inmates.GroupBy(c => (c.Species, c.Tier)).Count(g => g.Count() > 1);
            var value = id switch
            {
                "coin" => .6 * (.12 + paired * .05),
                "shackles" => .4 * Math.Max(meanGain, .075) * .75,
                "blacklist" => Math.Max(meanGain, .08) * 2 * (Sealed <= 6 ? .8 : .3),
                "scapegoat" => .11 + (Has("maiden") ? .03 : 0),
                "maiden" => .10 + .08 / Math.Max(1, Inmates.Count),
                "rope" => .065,
                "collar" => .8 * Math.Max(meanGain, .13) * .45,
                "revenge" => .3 * Math.Max(meanGain, .2),
                "cage" => .4 * Expected() * WOpen,
                "saw" => .25 * Math.Max(meanGain, .17),
                "whip" => .35 * Math.Max(meanGain, .12) - .008,
                "skewer" => .04 + Inmates.Count(c => c.Species == "rat") * .015,
                "muzzle" => Inmates.Count <= 4 ? .18 : .055,
                "tongs" => .08 + Inmates.Count(c => c.Species == "mammoth") * .02,
                "ironbox" => Expected(2) * .45,
                "keys" => .8 * .9 * Expected() * WOpen,
                "fire" => .35 * Expected() * WOpen,
                "hook" => Expected(1) - Expected(),
                "dogtag" => .25 * Math.Max(meanGain, .12),
                "stake" => .4 * (Tier(3) - Expected(0, true)),
                _ => throw new ArgumentException("尚未支持该刑具。")
            };
            return value * Days;
        }
        private double Gift(string id) => id switch
        {
            // Ring uses side opens; no Blacklist/Keys/Fire/Dogtag patrol bonus.
            "ring" => Math.Min(3, Doors.Count) * Expected(Has("hook") ? 1 : 0) * WOpen,
            "wine" => Inmates.Where(c => c.Tier < 3).Sum(c => Lift(c.Tier!.Value, 1)),
            "horn" => Inmates.Sum(c => Math.Max(0, Meals(c).Select(meal => Devour(c, meal, false)).DefaultIfEmpty(0).Max())) * .7,
            "brand" => Math.Min(4, Inmates.Count(c => Meals(c).Count == 0)) * .025,
            _ => throw new ArgumentException("尚未支持该珍藏。")
        };
        private double OfficeReward() => AverageLift(1);
        private double Instigate() => GameRules.InstigatableDoors(_st) > 0 ? EventPrior() * WInstigate * Math.Min(1, Days / 5d) : 0;
        private double Patrol(int destination, bool ratway)
        {
            if (destination == 0) return Math.Max(OfficeReward(), EventPrior() * WInstigate * Math.Min(1, Days / 5d));
            var room = _st.Rooms[destination];
            var value = 0d;
            var sideOpen = _st.ChainArmed && !ratway;
            if (room.Kind == "locked")
            {
                value += WOpen * (room.KnownTier is int known ? Tier(known) : Expected(Math.Max(room.KnownMinimumTier ?? 0, Has("hook") ? 1 : 0)));
                if (!sideOpen && Has("keys"))
                {
                    var adjacent = new[] { (destination + 15) % 16, (destination + 1) % 16 }.Count(n => n != 0 && _st.Rooms[n].Kind == "locked");
                    value += .8 * adjacent * Expected() * WOpen;
                }
                if (!sideOpen && Has("fire")) value *= 1.35;
                if (!sideOpen && Has("dogtag")) value += .25 * .05;
                if (!sideOpen && Has("blacklist") && Sealed <= 6) value += 2 * .12;
            }
            if (GameRules.IsInmate(room) && !_st.UsedAction)
            {
                var activation = _st.NoSwap ? 0 : Math.Max(0, Swap(room));
                if (!_st.NoDevour) activation = Math.Max(activation, Meals(room).Select(meal => Devour(room, meal, false)).DefaultIfEmpty(0).Max());
                value += activation;
                if (room.Ivory && Has("tongs") && !_st.NoSwap) value += .02;
            }
            // Entering the destination can resolve its incident even when a path effect opened it.
            if (room.Event) value += room.EventKind is not null ? Math.Max(0, EventWorth(room.EventKind)) : EventPrior();
            var steps = GameRules.StepsBetween(_st.Position, destination);
            if (_st.ChainArmed && !ratway)
            {
                var count = Doors.Count(c => GameRules.StepsBetween(_st.Position, c.Index) <= steps);
                value += count * Expected() * .6;
            }
            if (!ratway)
            {
                if (Has("shackles") && steps >= 4) value += .4 * .13 * WWalk;
                value += steps * .003 * WStep;
                if (_st.Objective == "walk" && !_st.Milestone) value += steps * .008;
            }
            return value;
        }
        public IEnumerable<AdviceCandidate> Actions()
        {
            var position = _st.Position;
            if (_st.Phase is "tool" or "gift")
            {
                foreach (var id in _st.OfferIds)
                    yield return new AdviceCandidate($"choose:{id}", $"选择{(_st.Phase == "tool" ? EngineCatalog.Tools[id] : EngineCatalog.Gifts[id])}",
                        "比较剩余天数、可见囚犯结构与当前工具效果；奖励选择不改变底层阶段。", _st.Phase == "tool" ? Tool(id) : Gift(id), position);
                yield break;
            }
            if (_st.Phase is "patrol" or "ratway")
            {
                var ratway = _st.Phase == "ratway";
                var destinations = ratway ? Enumerable.Range(1, 15) : _st.Destinations;
                foreach (var destination in destinations)
                    yield return new AdviceCandidate("patrol", destination == 0 ? "前往审讯室" : $"前往{destination + 1}号牢房",
                        ratway ? "鼠道允许前往任意非审讯室，不计步行收益。" : "比较开门、可执行吞噬、事件与移动加成；未知牢房按近似分布估值。", Patrol(destination, ratway), position, destination);
                yield break;
            }
            if (_st.Phase == "office")
            {
                yield return new AdviceCandidate("office:instigate", "煽动：添加一个事件", "最多增加一个事件，仍需今后巡逻到达。", Instigate(), position);
                yield return new AdviceCandidate("office:reward", "赏赐：随机一名囚犯升阶", "按场上每名囚犯随机提升一阶的平均价值估算。", OfficeReward(), position);
                yield break;
            }
            if (_st.Phase == "event")
            {
                yield return new AdviceCandidate("event:accept", "接受当前事件", EventReason(_st.EventKind!), EventWorth(_st.EventKind!) + .001, position);
                yield return new AdviceCandidate("event:skip", "跳过当前事件", "保留当前囚犯结构，放弃本次事件效果。", 0, position);
                yield break;
            }
            var eater = _st.Rooms[position];
            if (_st.Phase == "chain")
            {
                foreach (var meal in Meals(eater))
                    yield return new AdviceCandidate("chain", $"继续吞噬{meal.Index + 1}号牢房的囚犯", DevourReason(eater, meal, true), Devour(eater, meal, true), position, meal.Index);
                yield return new AdviceCandidate("stopchain", "停止连吃，保留囚犯", "本日吞噬次数保持已使用，保留现有食物和高阶囚犯。", 0, position);
                yield break;
            }
            yield return new AdviceCandidate("end", "结束本日", "保留当前囚犯结构，进入下一天；最后一天结束本局。", 0, position);
            if (!_st.UsedAction && GameRules.IsInmate(eater))
            {
                if (!_st.NoDevour) foreach (var meal in Meals(eater))
                    yield return new AdviceCandidate("devour", $"吞噬{meal.Index + 1}号牢房的囚犯", DevourReason(eater, meal, false), Devour(eater, meal, false), position, meal.Index);
                if (!_st.NoSwap) yield return new AdviceCandidate("swap", "替换当前囚犯", "比较替换新阶级的近似期望与失去当前囚犯的成本；当天与吞噬互斥。", Swap(eater), position);
            }
        }
        private string DevourReason(RoomState eater, RoomState meal, bool chain)
        {
            var gain = GameRules.GuaranteedDevourGain(_st, eater, meal);
            var text = $"同阶克制关系成立，确定升{gain}阶，食物会离场；吞噬后随机改变种族。";
            if (Has("saw") || Has("whip") || chain && Has("collar")) text += "另有刑具概率升阶，评分按其卡面概率估值。";
            if (Has("muzzle")) text += "口枷按吞后人数不超过三的待确认时点计算。";
            return text;
        }
        private static string EventReason(string id) => id switch
        {
            "usurp1" => "随机一名囚犯升一阶，并推进后续僭越。",
            "usurp2" => "随机一名囚犯升两阶，并推进后续僭越。",
            "usurp3" => "随机两名囚犯各升两阶，并推进后续僭越。",
            "usurp4" => "全场囚犯升两阶，达到绮彩者离场入库。",
            "chain" => "下次正常步行打开沿途封门；副开门不计主开门加成。",
            "execution" => "估算处刑提升与移除被克制囚犯的资源成本。",
            _ => "按已确认事件机制、当前人数和剩余天数估算收益。"
        };
    }
}
