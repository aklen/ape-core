using System.Collections.Concurrent;

namespace Ape.Core.Scene;

/// <summary>
/// Thread-safe registry for <see cref="IEntity"/> factories keyed by logical type id.
/// </summary>
public sealed class SceneEntityRegistry : ISceneEntityRegistry
{
    private readonly ConcurrentDictionary<string, Func<IServiceProvider, string, string?, IEntity>> _factories = new();

    public void Register(string typeId, Func<IServiceProvider, string, string?, IEntity> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        ArgumentNullException.ThrowIfNull(factory);
        if (!_factories.TryAdd(typeId, factory))
            throw new InvalidOperationException($"Scene entity type '{typeId}' is already registered.");
    }

    public bool TryCreate(string typeId, IServiceProvider services, string entityId, string? ownerId, out IEntity? entity)
    {
        entity = null;
        if (!_factories.TryGetValue(typeId, out var factory))
            return false;
        entity = factory(services, entityId, ownerId);
        return true;
    }

    public IReadOnlyCollection<string> RegisteredTypeIds => _factories.Keys.ToArray();
}
