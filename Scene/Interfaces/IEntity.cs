namespace Ape.Core.Scene;

/// <summary>
/// Base interface for all functional entities in the scene graph.
/// Entities represent functional objects that can be attached to transformation nodes.
/// The logical type is the registry <see cref="TypeId"/> string published by the owning module.
/// </summary>
public interface IEntity : IBase
{
    /// <summary>
    /// Registry type id (same string used with <see cref="ISceneEntityRegistry"/>),
    /// e.g. <c>builtin.light</c>.
    /// </summary>
    string TypeId { get; set; }
}
