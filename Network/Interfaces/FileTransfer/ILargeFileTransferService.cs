namespace Ape.Core.Network.FileTransfer;

/// <summary>
/// Service for transferring large files using chunk-based protocol.
/// Provides high-level API for uploading and downloading files.
/// </summary>
public interface ILargeFileTransferService
{
    /// <summary>
    /// Upload a file and return its unique identifier.
    /// The file is split into chunks and stored in content-addressed storage.
    /// </summary>
    /// <param name="filePath">Path to the file to upload</param>
    /// <param name="chunkSize">Size of each chunk in bytes (default: 64KB)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Unique file identifier (Guid)</returns>
    Task<Guid> UploadFileAsync(string filePath, int chunkSize = 64 * 1024, CancellationToken ct = default);

    /// <summary>
    /// Download a file by its identifier to a destination path.
    /// Chunks are requested from peers and reassembled.
    /// </summary>
    /// <param name="fileId">Unique file identifier</param>
    /// <param name="destinationPath">Path where the file should be saved</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Path to the downloaded file</returns>
    Task<string> DownloadFileAsync(Guid fileId, string destinationPath, CancellationToken ct = default);

    /// <summary>
    /// Get transfer progress (0.0 to 1.0).
    /// </summary>
    /// <param name="transferId">Transfer identifier (fileId)</param>
    /// <returns>Progress percentage (0.0 = 0%, 1.0 = 100%)</returns>
    float GetProgress(Guid transferId);

    /// <summary>
    /// Cancel an ongoing transfer.
    /// </summary>
    /// <param name="transferId">Transfer identifier (fileId)</param>
    void CancelTransfer(Guid transferId);

    /// <summary>
    /// Event fired when a chunk is received during download.
    /// Args: (fileId, chunkIndex)
    /// </summary>
    event Action<Guid, int>? OnChunkReceived;

    /// <summary>
    /// Event fired when a transfer completes successfully.
    /// Args: (fileId)
    /// </summary>
    event Action<Guid>? OnTransferComplete;

    /// <summary>
    /// Event fired when a transfer fails.
    /// Args: (fileId, errorMessage)
    /// </summary>
    event Action<Guid, string>? OnTransferFailed;
}
