using Ape.Core.Replication;

namespace Ape.Core.Scene;

/// <summary>
/// Registry of named factories for <see cref="IEntity"/> types. Domain modules register here so the host
/// can create extension entities without hard references to module implementation types.
/// </summary>
public interface ISceneEntityRegistry
{
    /// <summary>
    /// Registers a factory. <paramref name="entityId"/> is the unique scene key (same as <c>CreateRegisteredEntity</c> <c>name</c>).
    /// The returned entity should have <see cref="IReplica.Id"/> set to <paramref name="entityId"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">When <paramref name="typeId"/> is already registered.</exception>
    void Register(string typeId, Func<IServiceProvider, string, string?, IEntity> factory);

    /// <summary>
    /// Creates an entity using a registered factory.
    /// </summary>
    /// <returns><c>true</c> if <paramref name="typeId"/> was found; the entity may still be null if the factory returns null.</returns>
    bool TryCreate(string typeId, IServiceProvider services, string entityId, string? ownerId, out IEntity? entity);

    /// <summary>
    /// All registered logical type ids (populated when the host or modules call <see cref="Register"/>).
    /// </summary>
    IReadOnlyCollection<string> RegisteredTypeIds { get; }
}
