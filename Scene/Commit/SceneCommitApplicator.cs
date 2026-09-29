using Ape.Core.Determinism;
using Ape.Core.Logging;
using Ape.Core.Scene;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Applies <see cref="ISceneCommitRequest"/> to <see cref="ISceneManager"/> on the commit thread.
/// <para>
/// <b>Why mostly Node records here?</b> Core owns the <see cref="INode"/> transform API end-to-end.
/// </para>
/// <para>
/// <b>Entities:</b> <see cref="ISceneManager.CreateRegisteredEntity"/> and <see cref="ISceneManager.RemoveEntity"/> are also on the core
/// contract — we can commit <em>creation/removal by registry <c>typeId</c></em> without importing module DTOs.
/// Generic property sets use <see cref="SetSceneNodePropertyCommitRequest"/> / <see cref="SetSceneEntityPropertyCommitRequest"/> with
/// <see cref="ReplicaPropertyBinder"/> ([Key] properties only).
/// </para>
/// </summary>
public static class SceneCommitApplicator
{
    public static void Apply(ISceneManager scene, ISceneCommitRequest op, ILogger? logger)
    {
        using (SceneMutationScope.Begin())
            ApplyCore(scene, op, logger);
    }

    private static void ApplyCore(ISceneManager scene, ISceneCommitRequest op, ILogger? logger)
    {
        switch (op)
        {
            case CreateSceneNodeCommitRequest c:
            {
                var node = scene.CreateNode(c.NodeName, c.OwnerId);
                lock (node.SyncRoot)
                    node.Position = c.InitialPosition;
                logger?.LogDebug($"[SceneCommit] Created node '{c.NodeName}' at {c.InitialPosition}");
                break;
            }
            case SetSceneNodePositionCommitRequest s:
            {
                var node = ScenePathResolve.TryGetNode(scene, s.NodeId);
                if (node == null)
                {
                    logger?.LogWarning($"[SceneCommit] Set position: node '{s.NodeId}' not found");
                    break;
                }
                lock (node.SyncRoot)
                {
                    if (!ReplicaPropertyBinder.TrySet(node, "Position", s.Position, logger))
                        logger?.LogWarning($"[SceneCommit] Set position failed for '{s.NodeId}'");
                }
                logger?.LogDebug($"[SceneCommit] Set '{s.NodeId}' position {s.Position}");
                break;
            }
            case SetSceneNodePropertyCommitRequest p:
            {
                var node = ScenePathResolve.TryGetNode(scene, p.SceneKey);
                if (node == null)
                {
                    logger?.LogWarning($"[SceneCommit] Set node property: node '{p.SceneKey}' not found");
                    break;
                }
                lock (node.SyncRoot)
                {
                    if (!ReplicaPropertyBinder.TrySet(node, p.PropertyName, p.Value, logger))
                        logger?.LogWarning($"[SceneCommit] Set property '{p.PropertyName}' failed on '{p.SceneKey}'");
                }
                break;
            }
            case RemoveSceneNodeCommitRequest r:
                scene.RemoveNode(r.NodeId);
                logger?.LogDebug($"[SceneCommit] Removed node '{r.NodeId}'");
                break;

            case CreateRegisteredEntityCommitRequest e:
                scene.CreateRegisteredEntity(e.TypeId, e.Name, e.OwnerId);
                logger?.LogDebug($"[SceneCommit] CreateRegisteredEntity typeId={e.TypeId} name={e.Name}");
                break;
            case RemoveRegisteredEntityCommitRequest x:
                scene.RemoveEntity(x.Name);
                logger?.LogDebug($"[SceneCommit] RemoveEntity name={x.Name}");
                break;

            case SetSceneEntityPropertyCommitRequest e:
            {
                var entity = scene.GetEntity(e.SceneKey);
                if (entity == null)
                {
                    logger?.LogWarning($"[SceneCommit] Set entity property: entity '{e.SceneKey}' not found");
                    break;
                }
                lock (entity.SyncRoot)
                {
                    if (!ReplicaPropertyBinder.TrySet(entity, e.PropertyName, e.Value, logger))
                        logger?.LogWarning($"[SceneCommit] Set property '{e.PropertyName}' failed on entity '{e.SceneKey}'");
                }
                break;
            }

            default:
                logger?.LogWarning(
                    $"[SceneCommit] No applicator for {op.GetType().Name}. " +
                    "Define module-specific requests and a handler, or extend SceneCommitApplicator only for cross-cutting Core scene operations.");
                break;
        }
    }
}
