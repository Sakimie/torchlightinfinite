using System.Text.Json;
using Talos.Core;

// Independent audit adapter. It exercises the public API without adding any
// test hooks to the shipping Core assembly and has no network/process calls.
if (args.Length != 2)
    throw new ArgumentException("Usage: Talos.StrategyAudit fixtures.json results.json");
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var fixtures = JsonSerializer.Deserialize<List<Fixture>>(File.ReadAllText(args[0]), options)
    ?? throw new InvalidDataException("Missing fixture list.");
var rows = new List<object>();
foreach (var fixture in fixtures)
{
    try
    {
        var state = fixture.State.Deserialize<GameState>(options)
            ?? throw new InvalidDataException("Missing game state.");
        var errors = GameAdvisor.Validate(state);
        if (errors.Count > 0)
        {
            bool recommendRejected = false;
            try { GameAdvisor.Recommend(state); }
            catch (ArgumentException) { recommendRejected = true; }
            catch (InvalidOperationException) { recommendRejected = true; }
            rows.Add(new { fixture.Id, valid = false, errors, recommendRejected });
            continue;
        }
        var candidates = GameAdvisor.GetCandidates(state);
        var recommendation = GameAdvisor.Recommend(state);
        var eater = state.Rooms[state.Position];
        var guaranteedGains = state.Rooms.Where(meal => GameRules.CanEat(state, eater, meal))
            .Select(meal => new { target = meal.Index, gain = GameRules.GuaranteedDevourGain(state, eater, meal) }).ToArray();
        var executionTargets = GameRules.ExecutionTargets(state, eater).Select(room => room.Index).ToArray();
        rows.Add(new { fixture.Id, valid = true, candidates, recommendation, guaranteedGains, executionTargets });
    }
    catch (Exception error)
    {
        rows.Add(new { fixture.Id, valid = false, error = error.GetType().Name + ": " + error.Message, recommendRejected = true });
    }
}
var catalog = new
{
    tools = EngineCatalog.Tools.Keys, events = EngineCatalog.Events.Keys,
    gifts = EngineCatalog.Gifts.Keys, unsupported = EngineCatalog.Unsupported.Keys,
    totalDays = EngineCatalog.TotalDays, instigateEvents = EngineCatalog.InstigateEvents,
    muzzlePopulation = EngineCatalog.MuzzlePopulation,
    usurp = new[] { "usurp1", "usurp2", "usurp3", "usurp4" }.Select(id => new
    {
        id, count = GameRules.EventPromotion(id).Count, by = GameRules.EventPromotion(id).By
    })
};
File.WriteAllText(args[1], JsonSerializer.Serialize(new { framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, catalog, rows }, options));

public sealed record Fixture(string Id, JsonElement State);
