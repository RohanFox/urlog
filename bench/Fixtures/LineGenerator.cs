namespace Urlog.Bench.Fixtures;

/// <summary>A deterministic line source. One instance per fixture; state advances per line.</summary>
public abstract class LineGenerator
{
    /// <summary>Stable identifier recorded in the fixture metadata.</summary>
    public abstract string Name { get; }

    /// <summary>Bump when the output for a given seed changes; forces regeneration of cached fixtures.</summary>
    public abstract int Version { get; }

    public abstract ulong Seed { get; }

    public long Lines { get; protected set; }

    /// <summary>Lines that carry one of the deliberate oddities (IPv6, long UA, quotes, missing fields, ...).</summary>
    public long OddityLines { get; protected set; }

    /// <summary>Lines written so far per kind, for a generator whose kinds a test must be able to count; null otherwise.</summary>
    public virtual IReadOnlyDictionary<string, long>? Counts => null;

    /// <summary>Writes exactly one line, including its terminator.</summary>
    public abstract void WriteLine(ByteSink sink);
}
