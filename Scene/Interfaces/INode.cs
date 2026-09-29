using System.Numerics;

namespace Ape.Core.Scene;

/// <summary>
/// Interface for transformation nodes in the scene graph.
/// Represents a positioned, rotated, and scaled object that can have children.
/// </summary>
public interface INode : IBase
{
    /// <summary>
    /// Position in 3D space (local coordinates).
    /// </summary>
    Vector3 Position { get; set; }

    /// <summary>
    /// Orientation as quaternion (local rotation).
    /// </summary>
    Quaternion Orientation { get; set; }

    /// <summary>
    /// Scale factor (local scale).
    /// </summary>
    Vector3 Scale { get; set; }

    /// <summary>
    /// Visibility of this node.
    /// </summary>
    bool IsVisible { get; set; }

    /// <summary>
    /// Visibility of children.
    /// </summary>
    bool ChildrenVisible { get; set; }

    /// <summary>
    /// Get derived (world) position.
    /// Includes parent transformations.
    /// </summary>
    Vector3 GetDerivedPosition();

    /// <summary>
    /// Get derived (world) orientation.
    /// Includes parent transformations.
    /// </summary>
    Quaternion GetDerivedOrientation();

    /// <summary>
    /// Get derived (world) scale.
    /// Includes parent transformations.
    /// </summary>
    Vector3 GetDerivedScale();

    /// <summary>
    /// Get local model matrix (Position, Orientation, Scale).
    /// </summary>
    Matrix4x4 GetModelMatrix();

    /// <summary>
    /// Get derived (world) model matrix.
    /// Includes parent transformations.
    /// </summary>
    Matrix4x4 GetDerivedModelMatrix();

    /// <summary>
    /// Translate this node by a delta vector.
    /// </summary>
    void Translate(Vector3 delta, TransformationSpace space);

    /// <summary>
    /// Rotate this node by angle around axis.
    /// </summary>
    void Rotate(float angle, Vector3 axis, TransformationSpace space);
}

/// <summary>
/// Transformation space for translate/rotate operations.
/// </summary>
public enum TransformationSpace
{
    /// <summary>
    /// Local space (relative to node's orientation).
    /// </summary>
    Local,

    /// <summary>
    /// Parent space (relative to parent's orientation).
    /// </summary>
    Parent,

    /// <summary>
    /// World space (absolute coordinates).
    /// </summary>
    World
}
