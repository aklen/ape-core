using Ape.Core.Determinism;

namespace Ape.Core.Scene.Commit;

/// <summary>
/// Enqueue surface for <see cref="ISceneCommitRequest"/>. The host passes an <see cref="IFrameCommitBatch"/>
/// (this interface) into <c>OnHostFrame</c>. Async callbacks use <see cref="IIngress{T}"/>, not a process-wide sink.
/// </summary>
public interface ISceneCommitSink
{
    void Enqueue(ISceneCommitRequest request);
}
