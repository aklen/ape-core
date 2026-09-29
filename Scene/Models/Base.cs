using MessagePack;
using System.Collections.Generic;
using System.Linq;
using Ape.Core.Logging;
using Ape.Core.Scene;
using Ape.Core.Replication;

namespace Ape.Core.Scene.Models;

/// <summary>
/// Base implementation for all scene graph objects (Nodes and Entities).
/// Provides parent-child hierarchy management.
/// 
/// Nodes: Transform objects in the scene hierarchy (position, orientation, scale)
/// Entities: Functional objects that attach to Nodes (concrete types in Ape.Module.*).
/// </summary>
[MessagePackObject(AllowPrivate = true)]
[Union(0, typeof(Node))]
public abstract class Base : Replica, IBase
{
    [Key(100)]
    protected string? _parentId;  // ← Internal, managed by SetParentNode()
    
    [Key(101)]
    protected List<string> _childIds = new();
    
    [IgnoreMember]
    protected ISceneManager? _sceneManager;
    
    /// <summary>
    /// Set the SceneManager (injected by SceneManager during creation).
    /// </summary>
    [IgnoreMember]
    public ISceneManager? SceneManager
    {
        get => _sceneManager;
        set => _sceneManager = value;
    }
    
    /// <summary>
    /// Attach this object to a parent node.
    /// Handles detaching from old parent and attaching to new parent automatically.
    /// </summary>
    public virtual void SetParentNode(INode? parentNode)
    {
        // 1. Detach from old parent
        if (_parentId != null && _sceneManager != null)
        {
            var oldParent = _sceneManager.GetNode(_parentId);
            if (oldParent is Base oldBase)
            {
                oldBase.RemoveChildInternal(this.Id);
            }
        }
        
        // 2. Attach to new parent
        if (parentNode != null)
        {
            _parentId = parentNode.Id;
            if (parentNode is Base newBase)
            {
                newBase.AddChildInternal(this.Id);
            }
            
            // Update UniquePath based on new parent
            UpdateUniquePath();
            
            // Trigger property changed event for network sync
            SetProperty(ref _parentId, _parentId);
        }
        else
        {
            _parentId = null;
            UpdateUniquePath();
            SetProperty(ref _parentId, _parentId);
        }
    }
    
    /// <summary>
    /// Get the current parent node (if any).
    /// </summary>
    public virtual INode? GetParentNode()
    {
        return _parentId != null && _sceneManager != null 
            ? _sceneManager.GetNode(_parentId) 
            : null;
    }
    
    /// <summary>
    /// Detach from current parent node.
    /// Equivalent to SetParentNode(null).
    /// </summary>
    public virtual void DetachFromParent()
    {
        SetParentNode(null);
    }
    
    /// <summary>
    /// Get all child objects attached to this object.
    /// Can include both Nodes and Entities.
    /// </summary>
    public virtual List<IBase> GetChildren()
    {
        if (_sceneManager == null)
            return new List<IBase>();
        
        var children = new List<IBase>();
        foreach (var childId in _childIds)
        {
            // Try to get as Node first
            var child = _sceneManager.GetNode(childId) as IBase;
            
            // If not found as Node, try as Entity (Light, Camera, etc.)
            if (child == null)
            {
                // TODO: Add GetEntity() method to ISceneManager
                // For now, only Nodes can be children
            }
            
            if (child != null)
                children.Add(child);
        }
        return children;
    }
    
    /// <summary>
    /// Check if this object has any children.
    /// </summary>
    public virtual bool HasChildren()
    {
        return _childIds.Count > 0;
    }
    
    /// <summary>
    /// Add a child to this object's child list (internal use only).
    /// </summary>
    internal void AddChildInternal(string childId)
    {
        if (!_childIds.Contains(childId))
        {
            _childIds.Add(childId);
            // Don't trigger property changed - this is internal bookkeeping
        }
    }
    
    /// <summary>
    /// Remove a child from this object's child list (internal use only).
    /// </summary>
    internal void RemoveChildInternal(string childId)
    {
        _childIds.Remove(childId);
        // Don't trigger property changed - this is internal bookkeeping
    }
    
    /// <summary>
    /// Update UniquePath based on parent hierarchy.
    /// Generates hierarchical path like "/Root/CameraNode/MainCamera"
    /// </summary>
    protected virtual void UpdateUniquePath()
    {
        if (_parentId != null && _sceneManager != null)
        {
            var parent = _sceneManager.GetNode(_parentId);
            if (parent != null)
            {
                // Build hierarchical path from parent
                UniquePath = $"{parent.UniquePath}/{GetSimpleName()}";
            }
            else
            {
                UniquePath = $"/{GetSimpleName()}";
            }
        }
        else
        {
            // Root object
            UniquePath = $"/{GetSimpleName()}";
        }
    }
    
    /// <summary>
    /// Get simple name from Id (last component after last slash or whole Id).
    /// </summary>
    private string GetSimpleName()
    {
        if (Id.Contains('/'))
        {
            var lastSlash = Id.LastIndexOf('/');
            return Id.Substring(lastSlash + 1);
        }
        return Id;
    }
}
