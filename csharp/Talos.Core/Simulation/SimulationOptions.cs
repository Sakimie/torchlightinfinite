namespace Talos.Core.Simulation;

/// <summary>Practice-model settings. The product only simulates ordinary twenty-day runs.</summary>
public sealed record SimulationOptions
{
    public bool Rewards { get; init; } = true;
    public int StartTools { get; init; } = 1;
    public string Objective { get; init; } = "king";
}

/// <summary>Local, explicitly seeded dice; never the real game's random state.</summary>
internal sealed class SimulationRandom(int seed)
{
    private uint _state = unchecked((uint)seed) is 0 ? 0x9e3779b9u : unchecked((uint)seed);
    public uint NextUInt()
    {
        var x = _state;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _state = x == 0 ? 0x9e3779b9u : x;
        return _state;
    }
    public double Next() => NextUInt() / 4294967296d;
    public bool Chance(double probability) => Next() < probability;
    public T Pick<T>(IReadOnlyList<T> choices) => choices.Count > 0
        ? choices[(int)(Next() * choices.Count)] : throw new InvalidOperationException("随机选择没有候选项。");
    public List<T> Shuffle<T>(IEnumerable<T> source)
    {
        var result = source.ToList();
        for (var i = result.Count - 1; i > 0; i--)
        {
            var j = (int)(Next() * (i + 1));
            (result[i], result[j]) = (result[j], result[i]);
        }
        return result;
    }
}
