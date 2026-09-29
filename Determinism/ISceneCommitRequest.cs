namespace Ape.Core.Determinism;

/// <summary>
/// Marker for operations that should be applied by the authoritative scene commit runner (future <c>ISceneCommitSink</c> batch).
/// Module-specific DTOs implement this; the commit pipeline interprets concrete types.
/// </summary>
public interface ISceneCommitRequest
{
}
