using MessagePack;
using Ape.Core.Scene;

namespace Ape.Core.Scene.Models;

/// <summary>
/// Base class for entities that provide functional behavior in the scene graph.
/// Entities attach to Nodes for spatial positioning.
/// Unlike Nodes (which handle ONLY transformation), Entities handle functional properties.
/// Concrete subtypes live in <c>Ape.Module.*</c>; <see cref="Ape.Core.Replication.Replica.Serialize"/> uses runtime type, not polymorphic <c>Entity</c>.
/// </summary>
#pragma warning disable MsgPack005 // Union members are in Ape.Module.*; runtime serialization uses concrete type.
[MessagePackObject(AllowPrivate = true)]
public abstract partial class Entity : Base, IEntity
{
    /// <summary>
    /// Registry type id. Key 200 matches the former extension-id field so existing payloads still decode.
    /// </summary>
    [Key(200)]
    public string TypeId { get; set; }

    /// <summary>
    /// Constructor for derived entity types.
    /// </summary>
    /// <param name="typeId">Registry type id from the owning module (e.g. <c>builtin.light</c>).</param>
    protected Entity(string typeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        TypeId = typeId;
    }
}
#pragma warning restore MsgPack005
