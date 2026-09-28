using Ape.Core.Network.FileTransfer;
using System.Security.Cryptography;

namespace Ape.Core.Replication;

/// <summary>
/// File-based content-addressed storage for chunks.
/// Storage structure: {baseDir}/chunks/{hash[0..2]}/{hash}.chunk
/// </summary>
public class FileChunkStore : IChunkStore
{
    private readonly string _baseDir;
    
    public FileChunkStore(string baseDir)
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(Path.Combine(_baseDir, "chunks"));
    }
    
    public async Task<string> StoreChunkAsync(byte[] data, CancellationToken ct = default)
    {
        // Calculate SHA256 hash
        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        
        // Check if already exists (deduplication)
        if (HasChunk(hash))
            return hash;
        
        // Create subdirectory (first 2 chars of hash)
        var subDir = Path.Combine(_baseDir, "chunks", hash[..2]);
        Directory.CreateDirectory(subDir);
        
        // Write chunk file
        var chunkPath = Path.Combine(subDir, $"{hash}.chunk");
        await File.WriteAllBytesAsync(chunkPath, data, ct);
        
        return hash;
    }
    
    public async Task<byte[]?> GetChunkAsync(string hash, CancellationToken ct = default)
    {
        var chunkPath = GetChunkPath(hash);
        
        if (!File.Exists(chunkPath))
            return null;
        
        return await File.ReadAllBytesAsync(chunkPath, ct);
    }
    
    public bool HasChunk(string hash)
    {
        return File.Exists(GetChunkPath(hash));
    }
    
    public Task DeleteChunkAsync(string hash, CancellationToken ct = default)
    {
        var chunkPath = GetChunkPath(hash);
        
        if (File.Exists(chunkPath))
            File.Delete(chunkPath);
        
        return Task.CompletedTask;
    }
    
    public long GetStorageSize()
    {
        var chunksDir = Path.Combine(_baseDir, "chunks");
        
        if (!Directory.Exists(chunksDir))
            return 0;
        
        return Directory.EnumerateFiles(chunksDir, "*.chunk", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
    }
    
    private string GetChunkPath(string hash)
    {
        return Path.Combine(_baseDir, "chunks", hash[..2], $"{hash}.chunk");
    }
}
