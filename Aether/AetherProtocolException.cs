namespace Ape.Core.Aether;

/// <summary>A peer rejects the operation. Nothing from it is admitted.</summary>
public sealed class AetherProtocolException : Exception
{
    public AetherProtocolException(string message) : base(message)
    {
    }
}
