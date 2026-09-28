using MessagePack;
using System.Numerics;
using Ape.Core.Scene;

namespace Ape.Core.Scene.Models;

/// <summary>
/// Scene graph node representing a spatial transformation in 3D space.
/// Provides position, orientation (quaternion), and scale.
/// Nodes form hierarchical parent-child relationships managed by Base class.
/// All property setters automatically trigger PropertyChangedEvent for network sync.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
public partial class Node : Base, INode
{
    [IgnoreMember]
    private Vector3 _position = Vector3.Zero;
    
    [IgnoreMember]
    private Quaternion _orientation = Quaternion.Identity;
    
    [IgnoreMember]
    private Vector3 _scale = Vector3.One;
    
    [IgnoreMember]
    private bool _isVisible = true;
    
    [IgnoreMember]
    private bool _childrenVisible = true;
    
    [Key(1)]
    public Vector3 Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }
    
    [Key(2)]
    public Quaternion Orientation
    {
        get => _orientation;
        set => SetProperty(ref _orientation, value);
    }
    
    [Key(3)]
    public Vector3 Scale
    {
        get => _scale;
        set => SetProperty(ref _scale, value);
    }
    
    [Key(4)]
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
    
    [Key(5)]
    public bool ChildrenVisible
    {
        get => _childrenVisible;
        set => SetProperty(ref _childrenVisible, value);
    }
    
    /// <summary>
    /// Get world-space position by combining with parent transformations.
    /// </summary>
    public Vector3 GetDerivedPosition()
    {
        var parent = GetParentNode();
        if (parent == null)
            return Position;
        
        var parentPos = parent.GetDerivedPosition();
        var parentOrientation = parent.GetDerivedOrientation();
        var parentScale = parent.GetDerivedScale();
        
        // Transform local position by parent's transformation
        return parentPos + Vector3.Transform(Position * parentScale, parentOrientation);
    }
    
    /// <summary>
    /// Get world-space orientation by combining with parent orientations.
    /// </summary>
    public Quaternion GetDerivedOrientation()
    {
        var parent = GetParentNode();
        if (parent == null)
            return Orientation;
        
        var parentOrientation = parent.GetDerivedOrientation();
        return Quaternion.Concatenate(Orientation, parentOrientation);
    }
    
    /// <summary>
    /// Get world-space scale by combining with parent scales.
    /// </summary>
    public Vector3 GetDerivedScale()
    {
        var parent = GetParentNode();
        if (parent == null)
            return Scale;
        
        var parentScale = parent.GetDerivedScale();
        return Scale * parentScale;
    }
    
    /// <summary>
    /// Get local transformation matrix (TRS: Translation * Rotation * Scale).
    /// </summary>
    public Matrix4x4 GetModelMatrix()
    {
        return Matrix4x4.CreateScale(Scale) *
               Matrix4x4.CreateFromQuaternion(Orientation) *
               Matrix4x4.CreateTranslation(Position);
    }
    
    /// <summary>
    /// Get world-space transformation matrix by combining with parent transformations.
    /// </summary>
    public Matrix4x4 GetDerivedModelMatrix()
    {
        var parent = GetParentNode();
        if (parent == null)
            return GetModelMatrix();
        
        return GetModelMatrix() * parent.GetDerivedModelMatrix();
    }
    
    /// <summary>
    /// Translate this node by a delta vector in the specified coordinate space.
    /// </summary>
    public void Translate(Vector3 delta, TransformationSpace space)
    {
        switch (space)
        {
            case TransformationSpace.Local:
                // Transform delta by node's local orientation
                Position += Vector3.Transform(delta, Orientation);
                break;
            
            case TransformationSpace.Parent:
                // Delta is already in parent space
                Position += delta;
                break;
            
            case TransformationSpace.World:
                // Transform delta from world space to parent space
                var parent = GetParentNode();
                if (parent != null)
                {
                    var parentOrientationInv = Quaternion.Inverse(parent.GetDerivedOrientation());
                    Position += Vector3.Transform(delta, parentOrientationInv) / parent.GetDerivedScale();
                }
                else
                {
                    Position += delta;
                }
                break;
        }
    }
    
    /// <summary>
    /// Rotate this node by angle (radians) around axis in the specified coordinate space.
    /// </summary>
    public void Rotate(float angle, Vector3 axis, TransformationSpace space)
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
        
        switch (space)
        {
            case TransformationSpace.Local:
                // Rotate in local space (concatenate after current orientation)
                Orientation = Quaternion.Concatenate(rotation, Orientation);
                break;
            
            case TransformationSpace.Parent:
                // Rotate in parent space (concatenate before current orientation)
                Orientation = Quaternion.Concatenate(Orientation, rotation);
                break;
            
            case TransformationSpace.World:
                // Rotate in world space
                var parent = GetParentNode();
                if (parent != null)
                {
                    var parentOrientationInv = Quaternion.Inverse(parent.GetDerivedOrientation());
                    var localRotation = Quaternion.Concatenate(rotation, parentOrientationInv);
                    Orientation = Quaternion.Concatenate(Orientation, localRotation);
                }
                else
                {
                    Orientation = Quaternion.Concatenate(Orientation, rotation);
                }
                break;
        }
    }
}
