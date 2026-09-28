namespace Ape.Core.Network.FileTransfer;

/// <summary>
/// Content-addressed storage for file chunks.
/// Uses SHA256 hash as chunk identifier for automatic deduplication.
/// </summary>
public interface IChunkStore
{
    /// <summary>
    /// Store a chunk and return its content hash (SHA256).
    /// </summary>
    /// <param name="data">Chunk data</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>SHA256 hash of the chunk (hex string)</returns>
    Task<string> StoreChunkAsync(byte[] data, CancellationToken ct = default);

    /// <summary>
    /// Retrieve a chunk by its content hash.
    /// </summary>
    /// <param name="hash">SHA256 hash (hex string)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Chunk data or null if not found</returns>
    Task<byte[]?> GetChunkAsync(string hash, CancellationToken ct = default);

    /// <summary>
    /// Check if a chunk exists in storage.
    /// </summary>
    /// <param name="hash">SHA256 hash (hex string)</param>
    /// <returns>True if chunk exists</returns>
    bool HasChunk(string hash);

    /// <summary>
    /// Delete a chunk from storage (for garbage collection).
    /// </summary>
    /// <param name="hash">SHA256 hash (hex string)</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteChunkAsync(string hash, CancellationToken ct = default);

    /// <summary>
    /// Get total storage size in bytes.
    /// </summary>
    long GetStorageSize();
}
