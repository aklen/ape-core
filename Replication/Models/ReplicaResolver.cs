using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace Ape.Core.Replication;

/// <summary>
/// Custom MessagePack resolver for ApeCore types.
/// Registers formatters for System.Numerics types and falls back to standard resolver.
/// </summary>
public class ReplicaResolver : IFormatterResolver
{
    public static readonly IFormatterResolver Instance = new ReplicaResolver();

    private ReplicaResolver()
    {
    }

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        return FormatterCache<T>.Formatter;
    }

    private static class FormatterCache<T>
    {
        public static readonly IMessagePackFormatter<T>? Formatter;

        static FormatterCache()
        {
            Formatter = (IMessagePackFormatter<T>?)ReplicaResolverGetFormatterHelper.GetFormatter(typeof(T));
        }
    }
}

internal static class ReplicaResolverGetFormatterHelper
{
    internal static object? GetFormatter(Type t)
    {
        // MessagePack 3.x includes Vector3Formatter by default
        // Fallback to standard resolver using reflection
        var method = typeof(StandardResolver).GetMethod(nameof(IFormatterResolver.GetFormatter));
        if (method != null)
        {
            var genericMethod = method.MakeGenericMethod(t);
            return genericMethod.Invoke(StandardResolver.Instance, null);
        }

        return null;
    }
}
