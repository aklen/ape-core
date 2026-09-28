using Ape.Core.Replication;

namespace Ape.Core.Scene;

/// <summary>
/// Base interface for all scene graph objects (Nodes and Entities).
/// Provides parent-child hierarchy management.
/// </summary>
public interface IBase : IReplica
{
    /// <summary>
    /// Attach this object to a parent node.
    /// Handles detaching from old parent and attaching to new parent automatically.
    /// </summary>
    /// <param name="parentNode">Parent node to attach to, or null to detach</param>
    void SetParentNode(INode? parentNode);

    /// <summary>
    /// Get the current parent node (if any).
    /// </summary>
    /// <returns>Parent node or null if no parent</returns>
    INode? GetParentNode();

    /// <summary>
    /// Detach from current parent node.
    /// Equivalent to SetParentNode(null).
    /// </summary>
    void DetachFromParent();

    /// <summary>
    /// Get all child objects attached to this object.
    /// Can include both Nodes and Entities.
    /// </summary>
    /// <returns>List of child objects</returns>
    List<IBase> GetChildren();

    /// <summary>
    /// Check if this object has any children.
    /// </summary>
    /// <returns>True if has children, false otherwise</returns>
    bool HasChildren();
}
