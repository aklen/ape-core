namespace Ape.Core.Replication;

/// <summary>
/// Service for replica access control: subscription, authentication, authorization.
/// When no config: no-op (GetSubscribedPeers returns null = broadcast).
/// When config present: filters who receives replica updates; enforces auth at Subscribe time.
/// </summary>
public interface IReplicaAccessControlService
{
    /// <summary>
    /// Get peer IDs subscribed to this path. No permission re-check – already validated at Subscribe.
    /// Returns null to indicate broadcast (no filtering, send to all connected).
    /// </summary>
    IReadOnlyList<string>? GetSubscribedPeers(string replicaPath);

    /// <summary>
    /// Subscribe peer to path pattern. Permission checked HERE (once).
    /// Returns true if allowed; false if denied (with reason).
    /// </summary>
    bool TrySubscribe(string peerId, string pathPattern, out string? denyReason, string requiredPrivilege = "read");

    /// <summary>
    /// Unsubscribe peer from path pattern.
    /// </summary>
    void Unsubscribe(string peerId, string pathPattern);

    /// <summary>
    /// Remove peer from all subscriptions (call on disconnect).
    /// </summary>
    void UnsubscribeAll(string peerId);

    /// <summary>
    /// Check if a peer has the required privilege for a path (used by TrySubscribe; also for write validation).
    /// </summary>
    bool HasPrivilege(string peerId, string replicaPath, string privilege);

    /// <summary>
    /// Register peer. peerId = logical ID (e.g. LocalPeerId from handshake). Required before TrySubscribe.
    /// </summary>
    void RegisterPeer(string peerId);

    /// <summary>
    /// Unregister peer and remove from all subscriptions.
    /// </summary>
    void UnregisterPeer(string peerId);

    /// <summary>
    /// Apply implicit subscriptions from config when a peer connects.
    /// Call after RegisterPeer. For include mode: subscribes to subscription.paths.
    /// For exclude mode: peer is considered subscribed to all (no explicit patterns).
    /// </summary>
    void ApplyImplicitSubscribe(string peerId);
}
