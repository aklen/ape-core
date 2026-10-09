namespace Ape.Core.Aether;

/// <summary>
/// Total order for a field write. A greater Lamport wins.
/// Equal Lamport breaks the tie by raw UTF-8 order of <see cref="ActorId"/>, greater id winning.
/// </summary>
public readonly record struct FieldVersion(long Lamport, string ActorId) : IComparable<FieldVersion>
{
    public int CompareTo(FieldVersion other)
    {
        var byClock = Lamport.CompareTo(other.Lamport);
        return byClock != 0 ? byClock : string.CompareOrdinal(ActorId, other.ActorId);
    }

    public static bool operator >(FieldVersion left, FieldVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(FieldVersion left, FieldVersion right) => left.CompareTo(right) < 0;
}
