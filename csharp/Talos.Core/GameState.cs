namespace Talos.Core;

/// <summary>Only observed information. Unknown rooms remain unknown until reviewed.</summary>
public sealed record RoomState
{
    public int Index { get; init; }
    public string Kind { get; init; } = "unknown";
    public string? Species { get; init; }
    public int? Tier { get; init; }
    /// <summary>
    /// Waiting incident marker. A re-sealed current room may hold a new waiting incident
    /// while GameState.EventKind still names an earlier interrupted global incident.
    /// On an open current-event room a same/unknown marker denotes that current incident.
    /// </summary>
    public bool Event { get; init; }
    public string? EventKind { get; init; }
    public bool Ivory { get; init; }
    public int? NestDue { get; init; }
    /// <summary>
    /// Incident presence has been checked by passing this door or manual confirmation.
    /// Event=false and Seen=true means confirmed absence; both false means unknown.
    /// Merely seeing a lock in a screenshot does not establish this flag.
    /// </summary>
    public bool Seen { get; init; }
    /// <summary>Only a manually confirmed/scouted rarity; never an invented hidden inmate.</summary>
    public int? KnownTier { get; init; }
    public string? KnownSpecies { get; init; }
    /// <summary>Confirmed lower bound, for example an unrevealed door after the Hunt scout.</summary>
    public int? KnownMinimumTier { get; init; }
}

public sealed record GameState
{
    public int Day { get; init; } = 1;
    public int Position { get; init; }
    public string Phase { get; init; } = "patrol";
    public List<RoomState> Rooms { get; init; } = Enumerable.Range(0, 16)
        .Select(index => new RoomState { Index = index, Kind = index == 0 ? "office" : "unknown" }).ToList();
    public List<int> Destinations { get; init; } = [];
    public List<string> Tools { get; init; } = [];
    public int Vault { get; init; }
    public string Objective { get; init; } = "other";
    public int ObjectiveProgress { get; init; }
    public bool Milestone { get; init; }
    public bool UsedAction { get; init; }
    public bool NoDevour { get; init; }
    public bool NoSwap { get; init; }
    public bool ChainArmed { get; init; }
    public int ChainConsumed { get; init; }
    /// <summary>1..4 = available Usurpation stage; 5 = finished; 0 is unconfirmed.</summary>
    public int UsurpStage { get; init; } = 1;
    public string? EventKind { get; init; }
    public List<string> OfferIds { get; init; } = [];
    public string? ResumePhase { get; init; }
    public bool Confirmed { get; init; }
    public bool UnsupportedPresent { get; init; }
    public string Source { get; init; } = "manual";
}

public sealed record AdviceCandidate(string ActionId, string Label, string Reason, double Score,
    int? Room = null, int? Target = null)
{
    public double? StandardError { get; init; }
    public double? GapStandardError { get; init; }
    public double? SampleVariance { get; init; }
    public int Samples { get; init; }
}

public sealed record AdviceResult(AdviceCandidate Best, IReadOnlyList<AdviceCandidate> Alternatives,
    string StrategyId, string Limitations)
{
    public string ScoreKind { get; init; } = "heuristic";
    public int SamplesPerAction { get; init; }
    public bool Completed { get; init; } = true;
    public int Seed { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public string StopReason { get; init; } = "complete";
    public int InitialVault { get; init; }
    /// <summary>Model-only sampling seeds for reproducing completed paired rounds.</summary>
    public IReadOnlyList<int> SimulationSeeds { get; init; } = [];
}
