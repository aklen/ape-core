using System.Text;

namespace Ape.Core.Aether;

/// <summary>
/// Total order for a field write. A greater Lamport wins.
/// Equal Lamport breaks the tie by raw UTF-8 byte order of <see cref="ActorId"/>, greater id winning.
/// </summary>
public readonly record struct FieldVersion(long Lamport, string ActorId) : IComparable<FieldVersion>
{
    public int CompareTo(FieldVersion other)
    {
        var byClock = Lamport.CompareTo(other.Lamport);
        return byClock != 0 ? byClock : CompareUtf8(ActorId, other.ActorId);
    }

    private static int CompareUtf8(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        var length = Math.Min(leftBytes.Length, rightBytes.Length);
        for (var i = 0; i < length; i++)
        {
            var compared = leftBytes[i].CompareTo(rightBytes[i]);
            if (compared != 0)
                return compared;
        }

        return leftBytes.Length.CompareTo(rightBytes.Length);
    }

    public static bool operator >(FieldVersion left, FieldVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(FieldVersion left, FieldVersion right) => left.CompareTo(right) < 0;
}
