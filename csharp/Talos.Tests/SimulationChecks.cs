using System.Text.Json;
using Talos.Core;
using Talos.Core.Simulation;

namespace Talos.Tests;

internal static class SimulationChecks
{
    public static void Register(Checks suite)
    {
        suite.Add("simulation.confirmation unknown rooms and unsupported mechanisms are never sampled away", () =>
        {
            var board = CoreChecks.Board();
            Check.Throws<ArgumentException>(() => SimulationEngine.FromObserved(board with { Confirmed = false }, 1));
            Check.Throws<ArgumentException>(() => SimulationEngine.FromObserved(board with { UnsupportedPresent = true }, 1));
            Check.Throws<ArgumentException>(() => SimulationEngine.FromObserved(CoreChecks.Replace(board, new RoomState { Index = 3, Kind = "unknown" }), 1));
            Check.Throws<ArgumentException>(() => SimulationEngine.CreateNewGame(1, new() { StartTools = 21 }));
            Check.Throws<ArgumentException>(() => SimulationEngine.CreateNewGame(1, new() { StartTools = 2 }));
            Check.Throws<ArgumentException>(() => SimulationEngine.CreateNewGame(1, new() { Objective = "unsupported" }));
        });
        suite.Add("simulation.day twenty terminates without entering a twenty-first day", () =>
        {
            var game = SimulationEngine.FromObserved(CoreChecks.Board() with { Day = 20, Vault = 7 }, 7);
            game.Apply(Action(game, "end")); Check.True(game.IsEnded); Check.Equal(20, game.Day); Check.Equal(7, game.Vault);
            Check.Equal(0, game.GetCandidates().Count); Check.Equal(7, game.Playout());
            Check.Throws<InvalidOperationException>(() => game.Apply(new("end", "", "", 0)));
        });
        suite.Add("simulation.skewer rat eating a mammoth banks a rainbow and consumes the daily action", () =>
        {
            var state = Meal(3, "rat", "mammoth") with { Tools = ["skewer"], Vault = 7, Day = 20 };
            var game = SimulationEngine.FromObserved(state, 13); game.Apply(Action(game, "devour", 2));
            Check.Equal(8, game.Vault); var after = game.Observe();
            Check.Equal("locked", after.Rooms[1].Kind); Check.Equal("locked", after.Rooms[2].Kind); Check.True(after.UsedAction);
            Check.Equal(8, game.Playout()); Check.True(game.IsEnded);
        });
        suite.Add("simulation.muzzle counts population after eating and before banking", () =>
        {
            foreach (var population in new[] { 4, 5 })
            {
                var state = Meal(1) with { Tools = ["muzzle"] };
                for (var index = 3; index <= population; index++) state = CoreChecks.Replace(state, CoreChecks.Inmate(index, "rat", 0));
                var game = SimulationEngine.FromObserved(state, 91); game.Apply(Action(game, "devour", 2)); var after = game.Observe();
                Check.Equal(population == 4 ? 4 : 2, after.Rooms[1].Tier); Check.Equal(population - 1, after.Rooms.Count(GameRules.IsInmate));
                Check.Equal(0, game.Vault); Check.True(after.UsedAction);
            }
        });
        suite.Add("simulation.usurp second stage upgrades only one prisoner by two and advances waiting stages", () =>
        {
            var state = Meal(0) with { Phase = "event", EventKind = "usurp2", UsurpStage = 2 };
            state = CoreChecks.Replace(state, new RoomState { Index = 8, Kind = "locked", Event = true, EventKind = "usurp2" });
            var game = SimulationEngine.FromObserved(state, 9); game.Apply(Action(game, "event:accept")); var after = game.Observe();
            Check.True(after.Rooms.Where(GameRules.IsInmate).Select(room => room.Tier).Order().SequenceEqual(new int?[] { 0, 2 }));
            Check.Equal(3, after.UsurpStage); Check.Equal("usurp3", after.Rooms[8].EventKind); Check.True(after.EventKind is null);
        });
        suite.Add("simulation.instigation adds exactly one unknown event without leaking its identity", () =>
        {
            var game = SimulationEngine.FromObserved(CoreChecks.Board() with { Phase = "office" }, 64);
            game.Apply(Action(game, "office:instigate")); var observation = game.Observe();
            Check.Equal(1, observation.Rooms.Count(room => room.Event)); Check.Equal("manage", observation.Phase);
            Check.True(observation.Rooms.Where(room => room.Event).All(room => room.EventKind is null && room.KnownTier is null && room.Species is null));
        });
        suite.Add("simulation.whip rolls on eating and cannot upgrade a newly opened prisoner", () =>
        {
            var gains = new HashSet<int>();
            for (var seed = 1; seed <= 32; seed++)
            {
                var patrol = CoreChecks.Replace(CoreChecks.Board() with { Phase = "patrol", Destinations = [1], Tools = ["whip"] },
                    new RoomState { Index = 1, Kind = "locked", KnownTier = 0, KnownSpecies = "rat" });
                var opener = SimulationEngine.FromObserved(patrol, seed); opener.Apply(Action(opener, "patrol", 1));
                Check.Equal(0, opener.Observe().Rooms[1].Tier);
                var eater = SimulationEngine.FromObserved(Meal(0) with { Tools = ["whip"] }, seed);
                eater.Apply(Action(eater, "devour", 2)); var tier = eater.Observe().Rooms[1].Tier!.Value;
                Check.True(tier is 1 or 2); gains.Add(tier);
            }
            Check.True(gains.SetEquals([1, 2]), "Whip's additional eat roll was never exercised");
        });
        suite.Add("simulation.ring side openings suppress primary-opening tools", () =>
        {
            var state = CoreChecks.Board() with { Phase = "gift", ResumePhase = "manage", OfferIds = ["ring"], Tools = ["dogtag", "keys", "fire", "blacklist"] };
            state = state with { Rooms = state.Rooms.Select(room => room.Index == 0 ? room : room.Index <= 4
                ? room with { KnownTier = 3, KnownSpecies = "lion" } : room with { Kind = "empty" }).ToList() };
            for (var seed = 1; seed <= 16; seed++)
            {
                var game = SimulationEngine.FromObserved(state, seed); game.Apply(Action(game, "choose:ring")); var after = game.Observe();
                Check.Equal(3, after.Rooms.Count(GameRules.IsInmate)); Check.True(after.Rooms.Where(GameRules.IsInmate).All(room => room.Tier == 3));
                Check.Equal("manage", after.Phase); Check.Equal(0, game.Vault);
            }
        });
        suite.Add("simulation.chain path side openings preserve known rarity without primary bonuses", () =>
        {
            var state = CoreChecks.Board() with { Phase = "patrol", Destinations = [3], ChainArmed = true, Tools = ["dogtag", "keys", "fire", "blacklist"] };
            state = state with { Rooms = state.Rooms.Select(room => room.Index is >= 1 and <= 3 ? room with { KnownTier = 2, KnownSpecies = "lion" } : room).ToList() };
            for (var seed = 1; seed <= 16; seed++)
            {
                var game = SimulationEngine.FromObserved(state, seed); game.Apply(Action(game, "patrol", 3)); var after = game.Observe();
                Check.Equal(3, after.Rooms.Count(GameRules.IsInmate)); Check.True(after.Rooms.Where(GameRules.IsInmate).All(room => room.Tier == 2)); Check.False(after.ChainArmed);
            }
        });
        suite.Add("simulation.due nests hatch as rare rats without primary-opening effects", () =>
        {
            var state = CoreChecks.Replace(CoreChecks.Board() with { Tools = ["whip", "dogtag", "keys", "fire"] }, new RoomState { Index = 4, Kind = "nest", NestDue = 2 });
            var game = SimulationEngine.FromObserved(state, 18); game.Apply(Action(game, "end")); var after = game.Observe();
            Check.Equal(2, game.Day); Check.Equal("rat", after.Rooms[4].Species); Check.Equal(2, after.Rooms[4].Tier); Check.True(after.Rooms[4].NestDue is null);
            Check.Equal(1, after.Rooms.Count(GameRules.IsInmate));
        });
        suite.Add("simulation.pending choices preserve their underlying chain stage and action usage", () =>
        {
            var state = Meal(1) with { Phase = "tool", ResumePhase = "chain", ChainConsumed = 1, UsedAction = true, OfferIds = ["coin"] };
            var game = SimulationEngine.FromObserved(state, 5); game.Apply(Action(game, "choose:coin")); var after = game.Observe();
            Check.Equal("chain", after.Phase); Check.Equal(1, after.ChainConsumed); Check.True(after.UsedAction); Check.True(after.Tools.Contains("coin"));
            game.Apply(Action(game, "stopchain")); Check.True(game.Observe().UsedAction); Check.True(game.GetCandidates().All(action => action.ActionId == "end"));
        });
        suite.Add("simulation.a gift that banks the event-room prisoner preserves the pending global event", () =>
        {
            foreach (var stage in new[] { 1, 2 })
            {
                var state = Meal(3, "rat", "mammoth") with
                { Phase = "gift", ResumePhase = "event", EventKind = $"usurp{stage}", UsurpStage = stage, Tools = ["skewer"], OfferIds = ["horn"] };
                var game = SimulationEngine.FromObserved(state, 17); game.Apply(Action(game, "choose:horn")); Check.Equal(1, game.Vault);
                // Banking earns its own gift before the interrupted global event resumes.
                for (var count = 0; game.Observe().Phase == "gift"; count++)
                {
                    Check.True(count < 4); var queued = game.Observe(); Check.Equal("event", queued.ResumePhase); Check.Equal($"usurp{stage}", queued.EventKind);
                    game.Apply(Action(game, "choose:wine"));
                }
                var resumed = game.Observe(); Check.Equal("event", resumed.Phase); Check.Equal($"usurp{stage}", resumed.EventKind); Check.Equal("locked", resumed.Rooms[1].Kind);
                game.Apply(Action(game, "event:accept")); Check.Equal(stage + 1, game.Observe().UsurpStage); Check.Equal("manage", game.Observe().Phase);
            }
        });
        suite.Add("simulation.a reclosed door keeps its new event while the interrupted old event resolves", () =>
        {
            var state = CoreChecks.Replace(CoreChecks.Board() with { Position = 1, Phase = "event", EventKind = "usurp1", UsurpStage = 1 },
                new RoomState { Index = 1, Kind = "locked", Seen = true, Event = true, EventKind = "freedom" });
            var game = SimulationEngine.FromObserved(state, 83); var before = game.Observe();
            Check.Equal("usurp1", before.EventKind); Check.True(before.Rooms[1].Event); Check.Equal("freedom", before.Rooms[1].EventKind);
            game.Apply(Action(game, "event:accept")); var after = game.Observe();
            Check.Equal(2, after.UsurpStage); Check.Equal("manage", after.Phase); Check.True(after.EventKind is null);
            Check.Equal("locked", after.Rooms[1].Kind); Check.True(after.Rooms[1].Event); Check.Equal("freedom", after.Rooms[1].EventKind);
        });
        suite.Add("simulation.input and returned observations are detached and hidden futures remain private", () =>
        {
            var state = CoreChecks.Replace(CoreChecks.Board() with { Phase = "patrol", Destinations = [1] }, new RoomState { Index = 1, Kind = "locked", Event = true });
            var original = Json(state); var first = SimulationEngine.FromObserved(state, 1); var second = SimulationEngine.FromObserved(state, 999);
            Check.Equal(Json(first.Observe()), Json(second.Observe()), "Random hidden event identities leaked before entry");
            var observed = first.Observe(); Check.True(observed.Rooms.Where(room => room.Kind == "locked").All(room => room.Species is null && room.Tier is null && room.KnownSpecies is null && room.KnownTier is null));
            observed.Rooms.Clear(); observed.Tools.Add("coin"); observed.Destinations.Clear(); Check.Equal(16, first.Observe().Rooms.Count); Check.Equal(0, first.Observe().Tools.Count);
            Check.Equal(original, Json(state)); state.Rooms.Clear(); state.Destinations.Clear(); Check.Equal(16, first.Observe().Rooms.Count); Check.Equal(1, first.Observe().Destinations.Count);
        });
        suite.Add("simulation.confirmed seen doors without event markers never invent an event on entry", () =>
        {
            var state = CoreChecks.Board() with { Phase = "patrol", Destinations = [1] };
            state = state with { Rooms = state.Rooms.Select(room => room with { Seen = true }).ToList() };
            for (var seed = 1; seed <= 32; seed++)
            {
                var game = SimulationEngine.FromObserved(state, seed); Check.True(game.Observe().Rooms.All(room => !room.Event));
                game.Apply(Action(game, "patrol", 1)); var entered = game.Observe();
                Check.Equal("manage", entered.Phase); Check.True(entered.EventKind is null); Check.True(entered.Rooms.All(room => !room.Event));
            }
        });
        suite.Add("simulation.unseen event existence stays hidden until the route reveals its marker or destination", () =>
        {
            var state = CoreChecks.Board() with { Phase = "patrol", Destinations = [3] }; string? initial = null; var revealedAny = false;
            for (var seed = 1; seed <= 64; seed++)
            {
                var game = SimulationEngine.FromObserved(state, seed); var before = game.Observe();
                Check.True(before.Rooms.Where(room => room.Kind == "locked").All(room => !room.Seen && !room.Event && room.EventKind is null));
                initial ??= Json(before); Check.Equal(initial, Json(before), "Sampled hidden event existence leaked into the initial observation");
                game.Apply(Action(game, "patrol", 3)); var after = game.Observe();
                foreach (var index in new[] { 1, 2 })
                { Check.True(after.Rooms[index].Seen); Check.True(after.Rooms[index].EventKind is null, "Passing a door revealed an unopened event's identity"); revealedAny |= after.Rooms[index].Event; }
                if (after.Phase == "event") { Check.True(after.EventKind is not null); revealedAny = true; }
            }
            Check.True(revealedAny, "No latent event was revealed in the fixed local-model seed fixtures");
        });
        suite.Add("simulation.scouted rarity lower bounds constrain sampling without inventing revealed species", () =>
        {
            var state = CoreChecks.Replace(CoreChecks.Board() with { Phase = "patrol", Destinations = [1] }, new RoomState { Index = 1, Kind = "locked", KnownMinimumTier = 2 });
            for (var seed = 1; seed <= 16; seed++)
            {
                var game = SimulationEngine.FromObserved(state, seed); var before = game.Observe().Rooms[1];
                Check.Equal(2, before.KnownMinimumTier); Check.True(before.KnownTier is null && before.KnownSpecies is null);
                game.Apply(Action(game, "patrol", 1)); Check.True(game.Observe().Rooms[1].Tier is >= 2 and <= 4);
            }
        });
        suite.Add("simulation.rejecting an illegal first action does not consume random state", () =>
        {
            var state = CoreChecks.Board() with { Phase = "patrol", Destinations = [1] };
            var first = SimulationEngine.FromObserved(state, 42); var control = SimulationEngine.FromObserved(state, 42); var before = Json(first.Observe());
            Check.Throws<ArgumentException>(() => first.Apply(new("patrol", "forged", "", 999, 0, 15))); Check.Equal(before, Json(first.Observe()));
            first.Apply(Action(first, "patrol", 1)); control.Apply(Action(control, "patrol", 1)); Check.Equal(Json(control.Observe()), Json(first.Observe()));
        });
        suite.Add("simulation.complete seeded games always expose legal visible actions and end by day twenty", () =>
        {
            for (var seed = 1; seed <= 12; seed++)
            {
                var game = SimulationEngine.CreateNewGame(seed); var twin = SimulationEngine.CreateNewGame(seed); var priorDay = 1; var count = 0;
                while (!game.IsEnded)
                {
                    Check.True(++count < 1000, "Full game stalled"); var observation = game.Observe();
                    Check.True(observation.Day >= priorDay && observation.Day <= 20); priorDay = observation.Day;
                    Check.Equal(0, GameAdvisor.Validate(observation).Count); Check.Equal(Json(observation), Json(twin.Observe()));
                    var candidates = game.GetCandidates(); Check.True(candidates.Count > 0); Check.True(candidates.SequenceEqual(GameAdvisor.GetCandidates(observation)));
                    game.Apply(candidates[0]); twin.Apply(twin.GetCandidates()[0]);
                }
                Check.True(twin.IsEnded); Check.Equal(20, game.Day); Check.Equal(game.Vault, twin.Vault);
            }
        });
        suite.Add("simulation.incomplete action-limited trajectories fail instead of returning partial rewards", () =>
        {
            var game = SimulationEngine.CreateNewGame(123, new() { StartTools = 0 });
            Check.Throws<InvalidOperationException>(() => game.Playout(maxActions: 1)); Check.False(game.IsEnded);
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); Check.Throws<OperationCanceledException>(() => game.Playout(cancel.Token));
            Check.Throws<ArgumentOutOfRangeException>(() => game.Playout(maxActions: 0));
        });
        suite.Add("rollout.actual terminal outcomes distinguish eating from ending on the last day", () =>
        {
            var state = Meal(3, "rat", "mammoth") with { Day = 20, Vault = 7, Tools = ["skewer"] }; var original = Json(state);
            var result = RolloutAdvisor.Recommend(state, Options(4)); var all = All(result);
            Check.Equal(RolloutAdvisor.StrategyVersion, result.StrategyId); Check.Equal("expectedRainbows", result.ScoreKind); Check.Equal(7, result.InitialVault);
            Check.Equal(4, result.SamplesPerAction); Check.Equal(4, result.SimulationSeeds.Count); Check.True(result.Completed); Check.Equal("maxRounds", result.StopReason);
            Check.Equal("devour", result.Best.ActionId); Check.Near(8, result.Best.Score); Check.Near(7, all.Single(action => action.ActionId == "end").Score);
            Check.True(all.All(action => action.Samples == 4 && action.StandardError == 0)); Check.Equal(original, Json(state));
        });
        suite.Add("rollout.common seeds reproduce full trajectories means variance and paired gap error", () =>
        {
            var state = GameAdvisor.CreateExample() with { Day = 18, Objective = "other", Tools = ["saw", "whip"] }; var original = Json(state);
            var options = Options(5); var result = RolloutAdvisor.Recommend(state, options); Check.True(result.Completed); var actions = All(result);
            var outcomes = actions.Select(action => result.SimulationSeeds.Select(seed =>
            {
                var game = SimulationEngine.FromObserved(state, seed); game.Apply(action); return (double)game.Playout();
            }).ToArray()).ToArray();
            for (var index = 0; index < actions.Length; index++)
            {
                var row = outcomes[index]; var variance = Variance(row); Check.Near(row.Average(), actions[index].Score);
                Check.Near(variance, actions[index].SampleVariance!.Value); Check.Near(Math.Sqrt(variance / row.Length), actions[index].StandardError!.Value);
                var gaps = row.Select((value, n) => outcomes[0][n] - value).ToArray();
                Check.Near(Math.Sqrt(Variance(gaps) / row.Length), actions[index].GapStandardError!.Value);
            }
            var repeated = RolloutAdvisor.Recommend(state, options); Check.True(result.SimulationSeeds.SequenceEqual(repeated.SimulationSeeds)); Check.True(actions.SequenceEqual(All(repeated)));
            Check.Equal(original, Json(state));
        });
        suite.Add("rollout.one complete round leaves uncertainty unknown instead of claiming zero error", () =>
        {
            var result = RolloutAdvisor.Recommend(Meal(0) with { Day = 20 }, Options(1));
            Check.Equal(1, result.SamplesPerAction); Check.True(All(result).All(action => action.Samples == 1 && action.StandardError is null && action.SampleVariance is null && action.GapStandardError is null));
        });
        suite.Add("rollout.invalid inputs and incomplete playouts never silently fall back to heuristics", () =>
        {
            var state = CoreChecks.Board() with { Phase = "patrol", Destinations = [1, 2, 3] };
            Check.Throws<ArgumentException>(() => RolloutAdvisor.Recommend(state, Options(1) with { MaxRounds = 0 }));
            Check.Throws<ArgumentException>(() => RolloutAdvisor.Recommend(state, Options(1) with { TimeBudgetMs = 0 }));
            Check.Throws<ArgumentException>(() => RolloutAdvisor.Recommend(state with { Confirmed = false }, Options(1)));
            Check.Throws<InvalidOperationException>(() => RolloutAdvisor.Recommend(state, Options(1) with { MaxActionsPerGame = 1 }));
        });
        suite.Add("rollout.time budget commits only complete paired rounds or reports no result", () =>
        {
            var state = CoreChecks.Board() with { Phase = "patrol", Destinations = [1, 2, 3] };
            try
            {
                var result = RolloutAdvisor.Recommend(state, Options(10000) with { TimeBudgetMs = 1 });
                Check.False(result.Completed); Check.Equal("timeBudget", result.StopReason); Check.True(result.SamplesPerAction > 0);
                Check.Equal(result.SamplesPerAction, result.SimulationSeeds.Count); Check.True(All(result).All(action => action.Samples == result.SamplesPerAction));
                Check.Equal("expectedRainbows", result.ScoreKind);
            }
            catch (InvalidOperationException error) { Check.True(error.Message.Contains("预算", StringComparison.Ordinal), "Unexpected simulation failure: " + error.Message); }
        });
        suite.Add("rollout.cancellation stops computation and never changes the observed board", () =>
        {
            var state = GameAdvisor.CreateExample() with { Day = 1 }; var original = Json(state);
            using var immediate = new CancellationTokenSource(); immediate.Cancel();
            Check.Throws<OperationCanceledException>(() => RolloutAdvisor.Recommend(state, Options(10), immediate.Token));
            using var later = new CancellationTokenSource(); var canceler = new Thread(() => { Thread.Sleep(10); later.Cancel(); }) { IsBackground = true };
            canceler.Start();
            try { Check.Throws<OperationCanceledException>(() => RolloutAdvisor.Recommend(state, Options(10000) with { TimeBudgetMs = 1000 }, later.Token)); }
            finally { canceler.Join(); }
            Check.Equal(original, Json(state));
        });
    }

    private static GameState Meal(int tier, string eater = "mammoth", string meal = "lion") => CoreChecks.Replace(CoreChecks.Board() with { Position = 1 }, CoreChecks.Inmate(1, eater, tier), CoreChecks.Inmate(2, meal, tier));
    private static AdviceCandidate Action(SimulationEngine game, string id, int? target = null) => game.GetCandidates().Single(action => action.ActionId == id && (target is null || action.Target == target));
    private static RolloutOptions Options(int rounds) => new() { Seed = 271828, MaxRounds = rounds, TimeBudgetMs = 30000, MaxActionsPerGame = 2000 };
    private static AdviceCandidate[] All(AdviceResult result) => new[] { result.Best }.Concat(result.Alternatives).ToArray();
    private static string Json(object value) => JsonSerializer.Serialize(value);
    private static double Variance(double[] values) { var mean = values.Average(); return values.Sum(value => (value - mean) * (value - mean)) / (values.Length - 1); }
}
