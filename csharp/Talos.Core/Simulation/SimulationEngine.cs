namespace Talos.Core.Simulation;

/// <summary>
/// Original twenty-day simulator ported from this project's offline-engine.js, with the
/// later native rule corrections. Hidden outcomes and dice are private. Observe is the
/// only information supplied to the continuation policy. This is an approximate model,
/// not a measurement of live-game probabilities.
/// </summary>
public sealed class SimulationEngine
{
    public const string Version = EngineCatalog.SimulationVersion;
    private static readonly string[] Species = ["mammoth", "lion", "wolf", "rat"];
    private readonly SimulationRandom _random;
    private readonly Cell[] _cells = Enumerable.Range(0, 16).Select(n => new Cell(n)).ToArray();
    private readonly List<string> _tools = [];
    private readonly Queue<Reward> _pending = new();
    private readonly bool _rewards;
    private readonly string _objective;
    private int _day = 1, _vault, _position, _progress, _usurpStage = 1;
    private bool _milestone, _usedAction, _noDevour, _noSwap, _chainArmed;
    private string _phase = "patrol";
    private List<int> _destinations = [];
    private string? _activeEvent;
    private int _chainConsumed;
    private int _nextId = 1;

    private sealed class Inmate(int id, string species, int tier)
    {
        public int Id { get; } = id;
        public string Species { get; set; } = species;
        public int Tier { get; set; } = tier;
    }
    private sealed class Cell(int index)
    {
        public int Index { get; } = index;
        public bool Sealed = index != 0;
        public Inmate? Inmate;
        public int? HiddenTier;
        public int KnownMinimumTier;
        public string? HiddenSpecies;
        public bool TierKnown, SpeciesKnown, Seen, Ivory;
        public int? NestDue;
        public string? EventKind;
        public bool EventKnown;
    }
    private sealed record Reward(string Kind, List<string> Options);

    private SimulationEngine(int seed, bool rewards, string objective)
    {
        _random = new SimulationRandom(seed);
        _rewards = rewards;
        _objective = objective;
    }

    public int Day => _day;
    public int Vault => _vault;
    public bool IsEnded => _phase == "ended" && _pending.Count == 0;

    public static SimulationEngine CreateNewGame(int seed, SimulationOptions? options = null)
    {
        var settings = options ?? new SimulationOptions();
        if (!EngineCatalog.Objectives.ContainsKey(settings.Objective)) throw new ArgumentException("不支持此模拟任务。");
        if (settings.StartTools is < 0 or > 1) throw new ArgumentException("普通玩法起始刑具选择次数只支持0或1。");
        var game = new SimulationEngine(seed, settings.Rewards, settings.Objective);
        game.AddEvents(2);
        game.Dawn();
        for (var i = 0; i < settings.StartTools; i++) game.QueueTool();
        return game;
    }

    /// <summary>
    /// Copies confirmed observations. Unknown event types and incident presence at unseen
    /// doors are sampled; Seen=true/Event=false absence is preserved. No game RNG or
    /// unrevealed inmate identity is accepted from the caller.
    /// </summary>
    public static SimulationEngine FromObserved(GameState observation, int seed)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation = observation with
        {
            Rooms = observation.Rooms?.ToList()!, Tools = observation.Tools?.ToList()!,
            Destinations = observation.Destinations?.ToList() ?? [], OfferIds = observation.OfferIds?.ToList() ?? []
        };
        var errors = GameAdvisor.Validate(observation);
        if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors), nameof(observation));
        var game = new SimulationEngine(seed, true, observation.Objective)
        {
            _day = observation.Day, _vault = observation.Vault, _position = observation.Position,
            _progress = observation.ObjectiveProgress, _milestone = observation.Milestone,
            _usurpStage = observation.UsurpStage, _usedAction = observation.UsedAction,
            _noDevour = observation.NoDevour, _noSwap = observation.NoSwap,
            _chainArmed = observation.ChainArmed,
            _phase = observation.Phase is "tool" or "gift" ? observation.ResumePhase! : observation.Phase,
            _activeEvent = observation.EventKind,
            _chainConsumed = (observation.Phase is "tool" or "gift" ? observation.ResumePhase : observation.Phase) == "chain" ? observation.ChainConsumed : 0,
            _destinations = observation.Destinations.ToList()
        };
        game._tools.AddRange(observation.Tools);
        if (game._phase == "chain") game._usedAction = true;
        foreach (var room in observation.Rooms)
        {
            var cell = game._cells[room.Index];
            cell.Sealed = room.Kind is "locked" or "nest";
            if (room.Kind == "inmate") cell.Inmate = new Inmate(game._nextId++, room.Species!, room.Tier!.Value);
            cell.Ivory = room.Ivory;
            cell.NestDue = room.NestDue;
            cell.HiddenTier = room.KnownTier;
            cell.KnownMinimumTier = room.KnownMinimumTier ?? 0;
            cell.HiddenSpecies = room.KnownSpecies;
            cell.TierKnown = room.KnownTier.HasValue;
            cell.SpeciesKnown = room.KnownSpecies is not null;
            cell.Seen = room.Seen || room.Kind != "locked" || room.Event;
            cell.EventKnown = room.EventKind is not null;
            cell.EventKind = room.Event ? room.EventKind ?? game.RandomEvent() : null;
            if (room.Kind == "locked" && !cell.Seen && game._random.Chance(.05)) cell.EventKind = game.RandomEvent();
        }
        // On an open current room, its same/unknown marker is the displayed incident,
        // not a second copy. A re-sealed door may already hold a NEW future incident;
        // preserve that marker while the earlier global incident is still pending.
        var currentCell = game._cells[game._position];
        if (game._phase == "event" && !currentCell.Sealed &&
            (!currentCell.EventKnown || currentCell.EventKind == game._activeEvent))
        { currentCell.EventKind = null; currentCell.EventKnown = false; }
        if (observation.Phase is "tool" or "gift") game._pending.Enqueue(new Reward(observation.Phase, observation.OfferIds.ToList()));
        return game;
    }

    /// <summary>A detached visible snapshot. The RNG and unrevealed outcomes never leave the engine.</summary>
    public GameState Observe()
    {
        if (IsEnded) throw new InvalidOperationException("本局模拟已经结束，没有下一步观测。");
        return new GameState
        {
            Day = _day, Vault = _vault, Position = _position,
            Phase = _pending.TryPeek(out var reward) ? reward.Kind : _phase,
            ResumePhase = reward is null ? null : _phase,
            OfferIds = reward?.Options.ToList() ?? [], Destinations = _destinations.ToList(),
            Tools = _tools.ToList(), Objective = _objective, ObjectiveProgress = Math.Min(99, _progress),
            Milestone = _milestone, UsedAction = _usedAction, NoDevour = _noDevour, NoSwap = _noSwap,
            ChainArmed = _chainArmed, ChainConsumed = _chainConsumed, UsurpStage = _usurpStage,
            EventKind = _activeEvent, Confirmed = true, Source = "simulation",
            Rooms = _cells.Select(cell => new RoomState
            {
                Index = cell.Index, Kind = cell.Index == 0 ? "office" : cell.Inmate is not null ? "inmate" : cell.NestDue is not null ? "nest" : cell.Sealed ? "locked" : "empty",
                Species = cell.Inmate?.Species, Tier = cell.Inmate?.Tier, Ivory = cell.Ivory && cell.Inmate is not null,
                NestDue = cell.NestDue, Seen = cell.Seen,
                Event = cell.Seen && cell.EventKind is not null,
                EventKind = cell.Seen && cell.EventKnown ? cell.EventKind : null,
                KnownTier = cell.Sealed && cell.TierKnown ? cell.HiddenTier : null,
                KnownSpecies = cell.Sealed && cell.SpeciesKnown ? cell.HiddenSpecies : null,
                KnownMinimumTier = cell.Sealed && cell.NestDue is null && cell.KnownMinimumTier > 0 ? cell.KnownMinimumTier : null
            }).ToList()
        };
    }

    public IReadOnlyList<AdviceCandidate> GetCandidates() => IsEnded ? [] : GameAdvisor.GetCandidates(Observe());

    /// <summary>Rejects fabricated or stale actions before changing the simulated state.</summary>
    public void Apply(AdviceCandidate action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsEnded) throw new InvalidOperationException("本局模拟已经结束。");
        if (!GetCandidates().Any(candidate => SameAction(candidate, action))) throw new ArgumentException("模拟操作不属于当前合法候选。", nameof(action));
        ApplyLegal(action);
    }

    internal void ApplyLegal(AdviceCandidate action)
    {
        if (action.ActionId.StartsWith("choose:", StringComparison.Ordinal)) Choose(action.ActionId[7..]);
        else switch (action.ActionId)
        {
            case "patrol": Patrol(action.Target!.Value); break;
            case "devour": Devour(action.Target!.Value, false); break;
            case "chain": Devour(action.Target!.Value, true); break;
            case "stopchain": _chainConsumed = 0; _phase = "manage"; break;
            case "swap": Swap(); break;
            case "end": EndDay(); break;
            case "office:instigate": AddEvents(EngineCatalog.InstigateEvents); _phase = "manage"; break;
            case "office:reward": RandomUpgrades(1, 1); _phase = "manage"; break;
            case "event:accept": ResolveEvent(true); break;
            case "event:skip": ResolveEvent(false); break;
            default: throw new InvalidOperationException("模拟器未实现此合法操作。");
        }
        NormalizePhase();
    }

    public int Playout(CancellationToken cancellationToken = default, int maxActions = 2000)
    {
        if (maxActions is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxActions), "模拟操作上限须为1至10000。");
        return PlayoutCore(cancellationToken, maxActions, null);
    }

    internal int PlayoutCore(CancellationToken cancellationToken, int maxActions, Action? checkBudget)
    {
        for (var count = 0; !IsEnded; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkBudget?.Invoke();
            if (count >= maxActions) throw new InvalidOperationException("整局模拟超过操作上限，未完成的轨迹不会计入建议。");
            var choices = GetCandidates();
            if (choices.Count == 0) throw new InvalidOperationException("模拟器未找到合法后续操作。");
            ApplyLegal(choices[0]);
        }
        return Vault;
    }

    internal static bool SameAction(AdviceCandidate a, AdviceCandidate b) => a.ActionId == b.ActionId && a.Room == b.Room && a.Target == b.Target;
    private bool Has(string tool) => _tools.Contains(tool, StringComparer.Ordinal);
    private List<Cell> Inmates() => _cells.Skip(1).Where(c => c.Inmate is not null).ToList();
    private List<Cell> Doors() => _cells.Skip(1).Where(c => c.Sealed && c.NestDue is null).ToList();
    private Cell? Find(int id) => _cells.FirstOrDefault(c => c.Inmate?.Id == id);
    private string RandomEvent() => _random.Pick(EventPool());
    private List<string> EventPool()
    {
        var ids = EngineCatalog.Events.Keys.Where(id => !id.StartsWith("usurp", StringComparison.Ordinal)).ToList();
        if (_usurpStage <= 4) ids.Add($"usurp{_usurpStage}");
        return ids;
    }
    private int DrawTier(int minimum = 0, bool replacement = false)
    {
        var weights = replacement ? EngineCatalog.ReplacementWeights : EngineCatalog.OpenWeights;
        var value = _random.Next() * weights.Skip(minimum).Sum();
        for (var tier = minimum; tier < 5; tier++) { value -= weights[tier]; if (value < 0) return tier; }
        return Math.Clamp(minimum, 0, 4);
    }
    private Inmate NewInmate(string? species = null, int? tier = null, int minimum = 0, bool replacement = false)
        => new(_nextId++, species ?? _random.Pick(Species), tier ?? DrawTier(minimum, replacement));
    private bool CanEat(Cell eater, Cell meal)
    {
        if (eater == meal || eater.Inmate is null || meal.Inmate is null || eater.Inmate.Tier != meal.Inmate.Tier) return false;
        var first = BaseMeal(eater.Inmate.Species);
        return first == meal.Inmate.Species || eater.Inmate.Tier >= 3 &&
            (eater.Inmate.Species == "mammoth" && meal.Inmate.Species == "wolf" || eater.Inmate.Species == "lion" && meal.Inmate.Species == "rat") ||
            Has("skewer") && eater.Inmate.Species == "rat" && meal.Inmate.Species == "rat";
    }
    private static string BaseMeal(string species) => species switch { "mammoth" => "lion", "lion" => "wolf", "wolf" => "rat", _ => "mammoth" };
    private List<Cell> Meals(Cell eater) => Inmates().Where(meal => CanEat(eater, meal)).ToList();
    private void Reset(Cell cell)
    {
        cell.Inmate = null; cell.Sealed = true; cell.HiddenTier = null; cell.HiddenSpecies = null;
        cell.KnownMinimumTier = 0;
        cell.TierKnown = false; cell.SpeciesKnown = false; cell.Ivory = false; cell.NestDue = null;
        cell.EventKind = _random.Chance(.05) ? RandomEvent() : null;
        cell.EventKnown = false; cell.Seen = cell.EventKind is not null;
    }
    private void QueueTool()
    {
        if (!_rewards || _tools.Count == EngineCatalog.Tools.Count) return;
        // Exclude choices already queued: two simultaneous rewards cannot award a duplicate instrument.
        var offered = _pending.Where(r => r.Kind == "tool").SelectMany(r => r.Options).ToHashSet(StringComparer.Ordinal);
        var options = _random.Shuffle(EngineCatalog.Tools.Keys.Where(id => !Has(id) && !offered.Contains(id))).Take(3).ToList();
        if (options.Count > 0) _pending.Enqueue(new Reward("tool", options));
    }
    private void CheckObjective(int tier = -1)
    {
        if (_objective == "king" && tier >= EngineCatalog.SupremeTier) _progress = 1;
        var need = GameRules.ObjectiveNeed(_objective);
        if (!_milestone && need > 0 && _progress >= need) { _milestone = true; QueueTool(); }
    }
    private void Upgrade(int id, int amount)
    {
        var cell = Find(id);
        if (cell?.Inmate is null) return;
        cell.Inmate.Tier += amount;
        var tier = cell.Inmate.Tier;
        if (tier >= EngineCatalog.RainbowTier)
        {
            _vault++;
            Reset(cell);
            if (_rewards) _pending.Enqueue(new Reward("gift", EngineCatalog.Gifts.Keys.ToList()));
        }
        CheckObjective(tier);
    }
    private void RandomUpgrades(int count, int by)
    {
        var ids = _random.Shuffle(Inmates().Select(c => c.Inmate!.Id)).Take(count).ToList();
        foreach (var id in ids) Upgrade(id, by);
    }
    private bool Open(Cell cell, bool effects = true, bool patrol = false, int? minimum = null, Inmate? inmate = null)
    {
        if (cell.Index == 0 || !cell.Sealed || cell.NestDue is not null) return false;
        var floor = Math.Max(cell.KnownMinimumTier, minimum ?? (Has("hook") ? 1 : 0));
        var fixedTier = cell.HiddenTier;
        // A guaranteed event opening can raise a previously scouted low-tier door.
        if (minimum is int forced && fixedTier is int existing) fixedTier = Math.Max(forced, existing);
        cell.Inmate = inmate ?? NewInmate(cell.HiddenSpecies, fixedTier, floor);
        cell.Sealed = false; cell.HiddenTier = null; cell.HiddenSpecies = null;
        cell.TierKnown = false; cell.SpeciesKnown = false; cell.Seen = true;
        cell.KnownMinimumTier = 0;
        var id = cell.Inmate.Id;
        CheckObjective(cell.Inmate.Tier);
        if (!effects) return true;
        if (Has("dogtag") && _random.Chance(.25)) Upgrade(id, 1);
        // Whip is a consume roll; it never upgrades an opened inmate.
        if (Has("fire") && Find(id)?.Inmate is Inmate current && _random.Chance(.35))
        {
            var target = Doors();
            if (target.Count > 0) Open(_random.Pick(target), false, inmate: NewInmate(current.Species, current.Tier));
        }
        if (Has("keys") && _random.Chance(.8))
            foreach (var n in new[] { (cell.Index + 15) % 16, (cell.Index + 1) % 16 }) if (n != 0) Open(_cells[n], false);
        if (patrol && Has("blacklist") && _cells.Skip(1).Count(c => c.Sealed) <= 5) RandomUpgrades(2, 1);
        return true;
    }
    private void AddEvents(int count)
    {
        foreach (var cell in _random.Shuffle(Doors().Where(c => c.EventKind is null)).Take(count))
        { cell.EventKind = RandomEvent(); cell.EventKnown = false; cell.Seen = true; }
    }
    private void SetDestinations()
    {
        if (_phase == "ratway") { _destinations = Enumerable.Range(1, 15).ToList(); return; }
        _destinations = _random.Shuffle(Enumerable.Range(1, 6).Select(step => (_position + step) % 16))
            .Take(Has("whip") ? 2 : 3).OrderBy(n => GameRules.StepsBetween(_position, n)).ToList();
    }
    private void Dawn()
    {
        _usedAction = false; _noDevour = false; _noSwap = false; _chainConsumed = 0; _activeEvent = null;
        foreach (var cell in _cells.Skip(1).Where(c => c.NestDue <= _day).ToList())
        { cell.NestDue = null; Open(cell, false, inmate: NewInmate("rat", 2)); }
        if (Has("coin"))
        {
            var pairs = new List<(Cell First, Cell Second)>();
            var inmates = Inmates();
            for (var i = 0; i < inmates.Count; i++) for (var j = i + 1; j < inmates.Count; j++)
                if (inmates[i].Inmate!.Species == inmates[j].Inmate!.Species && inmates[i].Inmate!.Tier == inmates[j].Inmate!.Tier) pairs.Add((inmates[i], inmates[j]));
            if (pairs.Count > 0 && _random.Chance(.6))
            {
                var pair = _random.Pick(pairs);
                if (_random.Chance(.5)) pair = (pair.Second, pair.First);
                var id = pair.Second.Inmate!.Id;
                Reset(pair.First); Upgrade(id, 2);
            }
        }
        foreach (var cell in _cells) cell.Ivory = false;
        if (Has("tongs"))
        {
            var elephants = Inmates().Where(c => c.Inmate!.Species == "mammoth").ToList();
            if (elephants.Count > 0) _random.Pick(elephants).Ivory = true;
        }
        if (Has("ironbox"))
        {
            var free = Doors().Where(c => c.EventKind is null).ToList();
            if (free.Count > 0)
            {
                var cell = _random.Pick(free); cell.NestDue = _day + 2; cell.Seen = true;
                cell.HiddenTier = null; cell.HiddenSpecies = null; cell.TierKnown = false; cell.SpeciesKnown = false; cell.KnownMinimumTier = 0;
            }
        }
        _phase = "patrol";
        SetDestinations();
    }
    private void Patrol(int destination)
    {
        var ratway = _phase == "ratway";
        var distance = GameRules.StepsBetween(_position, destination);
        if (_chainArmed && !ratway)
        {
            for (var step = 1; step <= distance; step++) Open(_cells[(_position + step) % 16], false);
            _chainArmed = false;
        }
        // Walking reveals incident markers along the route, not their unseen types.
        if (!ratway) for (var step = 1; step <= distance; step++) _cells[(_position + step) % 16].Seen = true;
        _position = destination; _destinations = [];
        if (!ratway)
        {
            if (_objective == "walk") { _progress += distance; CheckObjective(); }
            if (Has("shackles") && distance >= 4 && _random.Chance(.4)) RandomUpgrades(1, 1);
        }
        if (destination == 0) _phase = "office";
        else
        {
            var cell = _cells[destination];
            Open(cell, true, !ratway);
            cell.Seen = true;
            if (cell.EventKind is not null && cell.Inmate is not null)
            { _activeEvent = cell.EventKind; cell.EventKind = null; cell.EventKnown = false; _phase = "event"; }
            else _phase = "manage";
        }
    }
    private int Eat(Cell eater, Cell meal, int biteNumber)
    {
        var inmate = eater.Inmate!;
        var id = inmate.Id;
        var gain = 1 + (Has("skewer") && inmate.Species == "rat" ? 1 : 0);
        // Population is counted after the meal disappears and before the eater banks.
        if (Has("muzzle") && Inmates().Count - 1 <= EngineCatalog.MuzzlePopulation) gain += 2;
        Reset(meal);
        if (_objective == "consume") { _progress++; CheckObjective(); }
        if (Has("collar") && biteNumber >= 2 && _random.Chance(.8)) gain++;
        if (Has("saw") && _random.Chance(.25)) gain++;
        if (Has("whip") && _random.Chance(.35)) gain++;
        inmate.Species = _random.Pick(Species);
        Upgrade(id, gain);
        if (Has("cage") && _random.Chance(.4)) Open(meal);
        var current = Find(id);
        if (current is not null && Has("revenge") && _random.Chance(.3))
        {
            var food = Species.Where(species => CanEat(current, new Cell(-1) { Inmate = new Inmate(-1, species, current.Inmate!.Tier) })).ToList();
            var doors = Doors();
            if (doors.Count > 0 && food.Count > 0) Open(_random.Pick(doors), inmate: NewInmate(_random.Pick(food), current.Inmate!.Tier));
        }
        return id;
    }
    private void Devour(int target, bool chain)
    {
        _usedAction = true;
        var id = Eat(_cells[_position], _cells[target], chain ? _chainConsumed + 1 : 1);
        var eater = Find(id);
        _chainConsumed = eater is not null && Meals(eater).Count > 0 ? chain ? _chainConsumed + 1 : 1 : 0;
        _phase = _chainConsumed > 0 ? "chain" : "manage";
    }
    private void Swap()
    {
        _usedAction = true;
        var cell = _cells[_position];
        var previous = cell.Inmate!;
        var ivory = cell.Ivory;
        var minimum = Has("rope") ? 2 : 0;
        var others = Inmates().Where(c => c != cell).ToList();
        if (Has("scapegoat") && others.Count >= 2)
        {
            foreach (var other in _random.Shuffle(others).Take(2)) Reset(other);
            minimum = Math.Min(5, Math.Max(minimum, previous.Tier + 1));
        }
        var replacement = NewInmate(tier: minimum >= 5 ? 5 : null, minimum: Math.Min(4, minimum), replacement: true);
        if (Has("stake") && previous.Tier < 3 && _random.Chance(.4)) replacement.Tier = Math.Max(3, replacement.Tier);
        cell.Inmate = replacement; cell.Ivory = false;
        if (replacement.Tier >= 5) Upgrade(replacement.Id, 0);
        else CheckObjective(replacement.Tier);
        if (Has("maiden") && Find(replacement.Id) is not null)
        {
            var peers = Inmates().Where(c => c != cell).ToList();
            if (peers.Count(c => c.Inmate!.Tier > replacement.Tier) == peers.Count(c => c.Inmate!.Tier < replacement.Tier)) Upgrade(replacement.Id, 3);
        }
        if (ivory && Has("tongs")) RandomUpgrades(4, 1);
    }
    private void EndDay()
    {
        if (_day == EngineCatalog.TotalDays) { _phase = "ended"; _destinations = []; }
        else { _day++; Dawn(); }
    }
    private void ResolveEvent(bool accept)
    {
        var kind = _activeEvent!;
        _activeEvent = null; _phase = "manage";
        if (!accept) return;
        if (_objective == "incident") { _progress++; CheckObjective(); }
        if (kind.StartsWith("usurp", StringComparison.Ordinal))
        {
            var (count, by) = GameRules.EventPromotion(kind);
            RandomUpgrades(count, by);
            _usurpStage = int.Parse(kind.AsSpan(5), System.Globalization.CultureInfo.InvariantCulture) + 1;
            foreach (var cell in _cells.Skip(1).Where(c => c.EventKind?.StartsWith("usurp", StringComparison.Ordinal) == true))
            { cell.EventKind = _usurpStage <= 4 ? $"usurp{_usurpStage}" : RandomEvent(); }
            return;
        }
        switch (kind)
        {
            case "chain": _chainArmed = true; break;
            case "execution":
            {
                var actor = _cells[_position].Inmate;
                if (actor is null) break;
                var victims = Inmates().Where(c => c.Index != _position && c.Inmate!.Tier < actor.Tier && c.Inmate.Species == BaseMeal(actor.Species)).ToList();
                foreach (var cell in victims) Reset(cell);
                Upgrade(actor.Id, victims.Count);
                break;
            }
            case "trickster":
            {
                var actor = _cells[_position].Inmate;
                if (actor is null) break;
                var counts = Species.ToDictionary(species => species, species => Inmates().Count(c => c.Inmate!.Species == species));
                actor.Species = _random.Pick(Species.Where(species => counts[species] == counts.Values.Min()).ToList());
                Upgrade(actor.Id, Math.Max(0, 4 - actor.Tier));
                break;
            }
            case "appetite":
                for (var n = 0; n < 5; n++)
                {
                    var inmates = Inmates();
                    if (inmates.Count == 0) break;
                    var tier = inmates.Min(c => c.Inmate!.Tier);
                    var actor = _random.Pick(inmates.Where(c => c.Inmate!.Tier == tier).ToList());
                    if (_random.Chance(.5)) Upgrade(actor.Inmate!.Id, 1);
                }
                break;
            case "convict":
                var locked = Doors(); if (locked.Count > 0) Open(_random.Pick(locked), minimum: 3); break;
            case "freedom": foreach (var cell in _random.Shuffle(Doors()).Take(3)) Open(cell); break;
            case "crow": case "hunt":
                foreach (var cell in _random.Shuffle(Doors()).Take(kind == "crow" ? 4 : 15))
                {
                    cell.HiddenTier ??= DrawTier(Math.Max(cell.KnownMinimumTier, Has("hook") ? 1 : 0));
                    // Only rarity is revealed. Species remains private until opening.
                    if (kind == "crow" || cell.HiddenTier <= 1) { cell.TierKnown = true; cell.Seen = true; }
                    else cell.KnownMinimumTier = Math.Max(2, cell.KnownMinimumTier);
                }
                break;
            case "ratway": _phase = "ratway"; SetDestinations(); break;
            default: throw new InvalidOperationException("模拟器没有实现当前事件，不能忽略后继续。");
        }
    }
    private void Choose(string id)
    {
        var reward = _pending.Dequeue();
        if (reward.Kind == "tool") { _tools.Add(id); return; }
        switch (id)
        {
            case "ring": foreach (var cell in _random.Shuffle(Doors()).Take(3)) Open(cell, false); break;
            case "wine": foreach (var inmateId in Inmates().Where(c => c.Inmate!.Tier < 3).Select(c => c.Inmate!.Id).ToList()) Upgrade(inmateId, 1); break;
            case "brand":
                foreach (var cell in _random.Shuffle(Inmates().Where(c => Meals(c).Count == 0)).Take(4))
                    cell.Inmate!.Species = _random.Pick(Species.Where(species => species != cell.Inmate.Species).ToList());
                break;
            case "horn":
                foreach (var inmateId in _random.Shuffle(Inmates().Select(c => c.Inmate!.Id)))
                {
                    var eater = Find(inmateId);
                    if (eater is null) continue;
                    var meals = Meals(eater);
                    if (meals.Count > 0) Eat(eater, _random.Pick(meals), 1);
                }
                break;
            default: throw new InvalidOperationException("模拟器未实现当前珍藏。");
        }
    }
    private void NormalizePhase()
    {
        if (_phase == "chain" && (_cells[_position].Inmate is null || Meals(_cells[_position]).Count == 0))
        { _phase = "manage"; _chainConsumed = 0; }
    }
}
