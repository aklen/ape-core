namespace Ape.Core.Network;

/// <summary>
/// Assembly-internal access to the profile key. This is deliberately absent from
/// INetworkManager so plugins cannot use the durable identity as a generic signing oracle.
/// </summary>
internal interface IPrincipalSigner
{
    bool HasPrincipalSigner { get; }

    bool TrySignPrincipal(ReadOnlySpan<byte> payload, out byte[] publicKey, out byte[] signature);
}
