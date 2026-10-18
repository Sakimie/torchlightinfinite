namespace Talos.Core;

/// <summary>Deterministic rule interpretations shared by validation and scoring; assumptions are in EngineCatalog.</summary>
public static class GameRules
{
    public static int StepsBetween(int from, int to) => (to - from + 16) % 16;
    public static (int Count, int By) EventPromotion(string id) => id switch
    {
        "usurp1" => (1, 1), "usurp2" => (1, 2), "usurp3" => (2, 2), "usurp4" => (15, 2),
        _ => throw new ArgumentException("此事件不是僭越升阶事件。", nameof(id))
    };
    public static int ObjectiveNeed(string id) => id switch { "king" => 1, "walk" => 18, "incident" => 2, "consume" => 4, _ => 0 };
    public static bool HasTool(GameState state, string id) => state.Tools?.Contains(id, StringComparer.Ordinal) == true;
    public static bool IsInmate(RoomState room) => room.Kind == "inmate" && room.Tier is >= 0 and <= 4 &&
        room.Species is not null && EngineCatalog.Species.ContainsKey(room.Species);
    public static bool CanEat(GameState state, RoomState eater, RoomState meal)
    {
        if (!IsInmate(eater) || !IsInmate(meal) || eater.Index == meal.Index || eater.Tier != meal.Tier) return false;
        var first = eater.Species switch { "mammoth" => "lion", "lion" => "wolf", "wolf" => "rat", "rat" => "mammoth", _ => null };
        if (first == meal.Species) return true;
        if (eater.Tier >= 3 && (eater.Species == "mammoth" && meal.Species == "wolf" || eater.Species == "lion" && meal.Species == "rat")) return true;
        return HasTool(state, "skewer") && eater.Species == "rat" && meal.Species == "rat";
    }
    public static int GuaranteedDevourGain(GameState state, RoomState eater, RoomState meal)
    {
        if (!CanEat(state, eater, meal)) throw new ArgumentException("目标不满足同阶克制关系。");
        var gain = 1;
        // A Rat's lawful consume gains +1, including its ordinary Mammoth meal.
        if (HasTool(state, "skewer") && eater.Species == "rat") gain++;
        var afterMeal = state.Rooms.Count(IsInmate) - 1;
        if (HasTool(state, "muzzle") && afterMeal <= EngineCatalog.MuzzlePopulation) gain += 2;
        return gain;
    }
    public static IReadOnlyList<RoomState> ExecutionTargets(GameState state, RoomState actor)
    {
        if (!IsInmate(actor)) return [];
        // Execution removes lower-tier countered inmates, not same-tier food.
        var countered = actor.Species switch { "mammoth" => "lion", "lion" => "wolf", "wolf" => "rat", "rat" => "mammoth", _ => null };
        return state.Rooms.Where(room => IsInmate(room) && room.Index != actor.Index &&
            room.Tier < actor.Tier && room.Species == countered).ToArray();
    }
    public static int InstigatableDoors(GameState state) => state.Rooms.Count(room => room.Kind == "locked" && !room.Event);
}
