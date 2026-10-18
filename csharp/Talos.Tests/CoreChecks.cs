using Talos.Core;

namespace Talos.Tests;

internal static class CoreChecks
{
    public static void Register(Checks suite)
    {
        suite.Add("core.requires confirmed complete 16-room observation", () =>
        {
            var valid = Board(); Check.Equal(0, GameAdvisor.Validate(valid).Count);
            Reject(valid with { Confirmed = false });
            Reject(valid with { Rooms = valid.Rooms.Take(15).ToList() });
            var duplicate = valid.Rooms.ToList(); duplicate[3] = duplicate[3] with { Index = 2 }; Reject(valid with { Rooms = duplicate });
            var unknown = valid.Rooms.ToList(); unknown[8] = unknown[8] with { Kind = "unknown" }; Reject(valid with { Rooms = unknown });
            Reject(GameAdvisor.CreateEmpty());
        });
        suite.Add("core.reject unsupported mechanisms and malformed observed inmates", () =>
        {
            var state = Board(); Reject(state with { UnsupportedPresent = true });
            foreach (var tool in EngineCatalog.Unsupported.Keys.Concat(["invented-tool"])) Reject(state with { Tools = [tool] });
            Reject(state with { Phase = "event", EventKind = "shared-fortune" });
            Reject(Replace(state, new RoomState { Index = 0, Kind = "office", EventKind = "shared-fortune" }));
            foreach (var inmate in new[] { Inmate(1, "dragon", 0), Inmate(1, "lion", 5), Inmate(1, "lion", -1), Inmate(1, "lion", null) })
                Reject(Replace(state, inmate));
            Reject(state with { Day = 0 }); Reject(state with { Day = 21 });
        });
        suite.Add("core.swallow ring legality matches every species and rarity boundary", () =>
        {
            var ring = new Dictionary<string, string> { ["mammoth"] = "lion", ["lion"] = "wolf", ["wolf"] = "rat", ["rat"] = "mammoth" };
            foreach (var tier in Enumerable.Range(0, 5))
            foreach (var eater in ring.Keys)
            foreach (var meal in ring.Keys)
            {
                var expected = ring[eater] == meal || tier >= 3 && (eater == "mammoth" && meal == "wolf" || eater == "lion" && meal == "rat");
                Check.Equal(expected, GameRules.CanEat(Board(), Inmate(1, eater, tier), Inmate(2, meal, tier)), $"{eater}->{meal} tier {tier}");
                Check.False(GameRules.CanEat(Board(), Inmate(1, eater, tier), Inmate(2, meal, (tier + 1) % 5)));
            }
            Check.False(GameRules.CanEat(Board(), Inmate(1, "mammoth", 0), Inmate(1, "lion", 0)));
            Check.False(GameRules.CanEat(Board(), Inmate(1, "mammoth", 0), new() { Index = 2, Kind = "locked" }));
        });
        suite.Add("core.skewer permits equal-tier rats and boosts any lawful rat meal", () =>
        {
            var state = Board() with { Tools = ["skewer"] };
            var rat = Inmate(1, "rat", 1); var mammoth = Inmate(2, "mammoth", 1); var secondRat = Inmate(3, "rat", 1);
            state = Replace(state, rat, mammoth, secondRat);
            Check.True(GameRules.CanEat(state, rat, secondRat));
            Check.False(GameRules.CanEat(state with { Tools = [] }, rat, secondRat));
            Check.Equal(2, GameRules.GuaranteedDevourGain(state, rat, mammoth));
            Check.Equal(2, GameRules.GuaranteedDevourGain(state, rat, secondRat));
        });
        suite.Add("core.muzzle after-meal three-person threshold and stacking", () =>
        {
            var eater = Inmate(1, "mammoth", 1); var meal = Inmate(2, "lion", 1);
            var four = Replace(Board() with { Tools = ["muzzle"] }, eater, meal, Inmate(3, "rat", 0), Inmate(4, "wolf", 0));
            Check.Equal(3, GameRules.GuaranteedDevourGain(four, eater, meal));
            var five = Replace(four, Inmate(5, "rat", 2)); Check.Equal(1, GameRules.GuaranteedDevourGain(five, eater, meal));
            var rat = Inmate(1, "rat", 1); var elephant = Inmate(2, "mammoth", 1);
            var stacked = Replace(Board() with { Tools = ["muzzle", "skewer"] }, rat, elephant);
            Check.Equal(4, GameRules.GuaranteedDevourGain(stacked, rat, elephant));
            Check.True(GameAdvisor.Recommend(four).Limitations.Contains(EngineCatalog.RuleAssumptions[0]), "Unverified muzzle timing must remain explicit");
        });
        suite.Add("core.usurp second event promotes exactly one inmate by two", () =>
        {
            Check.Equal((1, 1), GameRules.EventPromotion("usurp1"));
            Check.Equal((1, 2), GameRules.EventPromotion("usurp2"));
            Check.Equal((2, 2), GameRules.EventPromotion("usurp3"));
            Check.Equal((15, 2), GameRules.EventPromotion("usurp4"));
            var state = Replace(Board() with { Position = 1, Phase = "event", EventKind = "usurp2", UsurpStage = 2 }, Inmate(1, "rat", 2));
            Check.True(GameAdvisor.GetCandidates(state).Single(item => item.ActionId == "event:accept").Reason.Contains("一名囚犯升两阶"));
            Reject(state with { UsurpStage = 1 });
        });
        suite.Add("core.execution current interpretation is strictly lower primary-countered inmates", () =>
        {
            var actor = Inmate(1, "mammoth", 3);
            var state = Replace(Board(), actor, Inmate(2, "lion", 2), Inmate(3, "lion", 3), Inmate(4, "wolf", 2), Inmate(5, "rat", 0));
            Check.True(GameRules.ExecutionTargets(state, actor).Select(room => room.Index).SequenceEqual(new[] { 2 }));
            var rat = Inmate(1, "rat", 2); state = Replace(state with { Tools = ["skewer"] }, rat, Inmate(2, "rat", 1), Inmate(3, "mammoth", 1));
            Check.True(GameRules.ExecutionTargets(state, rat).Select(room => room.Index).SequenceEqual(new[] { 3 }));
        });
        suite.Add("core.instigation is one event and ignores nests or occupied slots", () =>
        {
            var rooms = Board().Rooms.Select(room => room.Index == 0 ? room : room with { Event = true }).ToList();
            rooms[1] = new() { Index = 1, Kind = "nest", NestDue = 3 };
            var state = Board() with { Phase = "office", Position = 0, Rooms = rooms };
            Check.Equal(1, EngineCatalog.InstigateEvents); Check.Equal(0, GameRules.InstigatableDoors(state));
            Check.Near(0, GameAdvisor.GetCandidates(state).Single(item => item.ActionId == "office:instigate").Score);
            rooms[2] = rooms[2] with { Event = false }; Check.Equal(1, GameRules.InstigatableDoors(state));
            Check.True(GameAdvisor.GetCandidates(state).Single(item => item.ActionId == "office:instigate").Score > 0);
        });
        suite.Add("core.eight phases expose only their legal action families", () =>
        {
            var manage = Replace(Board() with { Position = 1 }, Inmate(1, "mammoth", 1), Inmate(2, "lion", 1));
            AssertActions(manage, ["end", "devour", "swap"]);
            AssertActions(manage with { Phase = "patrol", Destinations = [2, 4] }, ["patrol"]);
            AssertActions(manage with { Phase = "office", Position = 0 }, ["office:instigate", "office:reward"]);
            AssertActions(manage with { Phase = "event", EventKind = "crow" }, ["event:accept", "event:skip"]);
            AssertActions(manage with { Phase = "tool", ResumePhase = "manage", OfferIds = ["coin", "muzzle"] }, ["choose:coin", "choose:muzzle"]);
            AssertActions(manage with { Phase = "gift", ResumePhase = "manage", OfferIds = ["wine", "ring"] }, ["choose:wine", "choose:ring"]);
            AssertActions(manage with { Phase = "chain", ChainConsumed = 1, UsedAction = true }, ["chain", "stopchain"]);
            var ratway = GameAdvisor.GetCandidates(manage with { Phase = "ratway" });
            Check.Equal(15, ratway.Count); Check.True(ratway.All(item => item.ActionId == "patrol" && item.Target is >= 1 and <= 15));
        });
        suite.Add("core.daily action exhaustion and event restrictions cannot create extra meals", () =>
        {
            var state = Replace(Board() with { Position = 1 }, Inmate(1, "mammoth", 1), Inmate(2, "lion", 1));
            AssertActions(state with { UsedAction = true }, ["end"]);
            AssertActions(state with { NoDevour = true }, ["end", "swap"]);
            AssertActions(state with { NoSwap = true }, ["end", "devour"]);
            Reject(state with { Phase = "chain", ChainConsumed = 0 });
            Reject(state with { Phase = "chain", ChainConsumed = 2 });
            Reject(state with { Phase = "office" });
        });
        suite.Add("core.pending rewards preserve and validate the underlying stage", () =>
        {
            var state = Board() with { Phase = "tool", ResumePhase = "patrol", Destinations = [1, 3], OfferIds = ["coin", "muzzle"] };
            Check.Equal(0, GameAdvisor.Validate(state).Count);
            Reject(state with { ResumePhase = null }); Reject(state with { Destinations = [] });
            Reject(state with { OfferIds = [] }); Reject(state with { OfferIds = ["coin", "coin"] });
            Reject(state with { Tools = ["coin"] });
            Reject(state with { OfferIds = ["lion-trap"] });
        });
        suite.Add("core.wraparound patrol range and known observation fields stay explicit", () =>
        {
            Check.Equal(2, GameRules.StepsBetween(15, 1));
            var state = Board() with { Phase = "patrol", Position = 15, Destinations = [0, 1, 5] };
            Check.Equal(0, GameAdvisor.Validate(state).Count);
            Reject(state with { Destinations = [6] }); Reject(state with { Destinations = [15] });
            var known = state.Rooms.ToList(); known[1] = known[1] with { KnownTier = 4, KnownSpecies = "lion" };
            Check.Equal(0, GameAdvisor.Validate(state with { Rooms = known }).Count);
            known[1] = known[1] with { Kind = "empty" }; Reject(state with { Rooms = known });
        });
        suite.Add("core.scores are finite deterministic and inputs remain unchanged", () =>
        {
            var state = GameAdvisor.CreateExample(); var before = System.Text.Json.JsonSerializer.Serialize(state);
            var first = GameAdvisor.GetCandidates(state); var second = GameAdvisor.GetCandidates(state);
            Check.True(first.SequenceEqual(second)); Check.True(first.All(item => double.IsFinite(item.Score)));
            Check.Equal(before, System.Text.Json.JsonSerializer.Serialize(state));
            var result = GameAdvisor.Recommend(state);
            Check.Equal(first[0], result.Best); Check.Equal(EngineCatalog.StrategyVersion, result.StrategyId);
            Check.True(result.Limitations.Contains("未做整局训练验证"));
        });
    }

    internal static GameState Board() => new()
    {
        Phase = "manage", Confirmed = true, UsurpStage = 1,
        Rooms = Enumerable.Range(0, 16).Select(index => new RoomState { Index = index, Kind = index == 0 ? "office" : "locked" }).ToList()
    };
    internal static RoomState Inmate(int index, string species, int? tier) => new() { Index = index, Kind = "inmate", Species = species, Tier = tier };
    internal static GameState Replace(GameState state, params RoomState[] rooms)
    {
        var result = state.Rooms.ToList(); foreach (var room in rooms) result[room.Index] = room;
        return state with { Rooms = result };
    }
    private static void Reject(GameState state)
    {
        Check.True(GameAdvisor.Validate(state).Count > 0, "Invalid observation was accepted");
        Check.Throws<ArgumentException>(() => GameAdvisor.Recommend(state));
    }
    private static void AssertActions(GameState state, string[] expected)
    {
        var candidates = GameAdvisor.GetCandidates(state);
        Check.True(candidates.Select(item => item.ActionId).ToHashSet().SetEquals(expected), string.Join(",", candidates.Select(item => item.ActionId)));
        Check.True(candidates.All(item => double.IsFinite(item.Score)));
    }
}
