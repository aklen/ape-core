namespace Ape.Core.Replication;

/// <summary>
/// Marks a property as a large file that should be transferred using chunk-based protocol.
/// The property value is a CAS reference (e.g., "cas:abc123...") or local file path.
/// When the property changes, the file is automatically chunked and transferred via INetworkTransport.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class FileChunkTransferAttribute : Attribute
{
    /// <summary>
    /// Chunk size in bytes (default: 64KB).
    /// </summary>
    public int ChunkSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Maximum number of parallel chunk transfers (default: 4).
    /// </summary>
    public int MaxParallelChunks { get; set; } = 4;

    /// <summary>
    /// Whether to automatically start download when property is set remotely (default: true).
    /// </summary>
    public bool AutoDownload { get; set; } = true;
}
