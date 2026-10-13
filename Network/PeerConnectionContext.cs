namespace Ape.Core.Network;

/// <summary>
/// One live socket and the principal bound to it.
/// <see cref="ConnectionId"/> is only the receiver-local slot used to send.
/// <see cref="PrincipalId"/> is the remote profile's public-key fingerprint, or a claim when no key was checked.
/// It is not the process instance id.
/// <see cref="IsAuthenticated"/> is true only after the signed handshake checks.
/// That proof is not by itself create, owner, or delete authority.
/// </summary>
public readonly record struct PeerConnectionContext(
    string ConnectionId,
    string PrincipalId,
    bool IsAuthenticated);
