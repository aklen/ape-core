namespace Ape.Core.Graph;

/// <summary>
/// Thrown by <see cref="ComponentGraph{TScratch}.Compile"/> when the authoring graph cannot be frozen.
/// Codes are stable (<c>CG101</c>…) so tests and agents can match them.
/// </summary>
public sealed class GraphCompileException : InvalidOperationException
{
    public GraphCompileException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}
