namespace Ape.Core.Network;

/// <summary>
/// One live socket and the principal bound to it.
/// <see cref="ConnectionId"/> is only the receiver-local slot used to send.
/// <see cref="PrincipalId"/> is the remote durable principal claimed at connect time.
/// It is not the process instance id and it is not a cryptographic proof.
/// <see cref="IsAuthenticated"/> stays false until a signed handshake or a key fingerprint exists.
/// Create, owner, and delete authority do not use this binding.
/// </summary>
public readonly record struct PeerConnectionContext(
    string ConnectionId,
    string PrincipalId,
    bool IsAuthenticated);
