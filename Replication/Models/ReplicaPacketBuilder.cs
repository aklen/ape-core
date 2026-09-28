namespace Ape.Core.Replication;

/// <summary>
/// Packet type flags for replica synchronization.
/// </summary>
public enum ReplicaPacketType : byte
{
    /// <summary>
    /// Full sync packet sent during initial connection.
    /// Contains complete replica data for newly connected peers.
    /// </summary>
    FullSync = 0x00,
    
    /// <summary>
    /// Create packet sent when a new replica is registered at runtime.
    /// Contains complete replica data for newly created objects.
    /// </summary>
    Create = 0x01,
    
    /// <summary>
    /// Update packet sent when replica properties change.
    /// Contains only changed property data (delta update).
    /// </summary>
    Update = 0x02,
    
    /// <summary>
    /// Delete packet sent when a replica is unregistered.
    /// Contains only the replica GUID, no payload.
    /// </summary>
    Delete = 0x03,

    /// <summary>
    /// Control plane: client requests subscription to a path pattern.
    /// Payload: path pattern string (e.g. "/Root/Players/*").
    /// </summary>
    Subscribe = 0x04,

    /// <summary>
    /// Control plane: client requests unsubscription from a path pattern.
    /// Payload: path pattern string.
    /// </summary>
    Unsubscribe = 0x05,

    /// <summary>
    /// Video frame packet (adaptive streaming). Not processed by ReplicaManager.
    /// Payload: [4 streamId][4 frameIndex][4 quality][N jpeg bytes]
    /// </summary>
    VideoFrame = 0x10
}

/// <summary>
/// Utility class for building and parsing replica synchronization packets.
/// 
/// Packet format (string-based IDs):
/// [1 byte TYPE][4 bytes ID length][N bytes ID][4 bytes OwnerId length][N bytes OwnerId][4 bytes type name length][N bytes type name][MessagePack data]
/// 
/// For Create/Update/FullSync:
///   payload = [4 bytes ID length][N bytes ID][4 bytes OwnerId length][N bytes OwnerId][4 bytes type name length][N bytes type name][MessagePack data]
/// 
/// For Delete:
///   payload = [4 bytes ID length][N bytes ID]
/// 
/// For Subscribe/Unsubscribe:
///   payload = [4 bytes path length][N bytes path pattern UTF-8]
/// </summary>
public static class ReplicaPacketBuilder
{
    /// <summary>
    /// Build a packet for replica creation, update, or full sync.
    /// </summary>
    public static byte[] BuildPacket(ReplicaPacketType packetType, IReplica replica)
    {
        if (packetType == ReplicaPacketType.Delete)
        {
            throw new ArgumentException("Use BuildDeletePacket() for Delete packets");
        }
        
        var data = replica.Serialize();
        var typeName = replica.GetType().AssemblyQualifiedName ?? replica.GetType().FullName ?? "Unknown";
        var typeNameBytes = System.Text.Encoding.UTF8.GetBytes(typeName);
        var ownerIdBytes = System.Text.Encoding.UTF8.GetBytes(replica.OwnerId);
        var idBytes = System.Text.Encoding.UTF8.GetBytes(replica.Id);
        
        // Packet format: [1 byte TYPE][4 bytes ID length][N bytes ID][4 bytes OwnerId length][N bytes OwnerId][4 bytes type name length][N bytes type name][MessagePack payload]
        var packet = new byte[1 + 4 + idBytes.Length + 4 + ownerIdBytes.Length + 4 + typeNameBytes.Length + data.Length];
        var offset = 0;
        
        // Write packet type
        packet[offset] = (byte)packetType;
        offset += 1;
        
        // Write ID length
        BitConverter.TryWriteBytes(packet.AsSpan(offset), idBytes.Length);
        offset += 4;
        
        // Write ID
        Array.Copy(idBytes, 0, packet, offset, idBytes.Length);
        offset += idBytes.Length;
        
        // Write OwnerId length
        BitConverter.TryWriteBytes(packet.AsSpan(offset), ownerIdBytes.Length);
        offset += 4;
        
        // Write OwnerId
        Array.Copy(ownerIdBytes, 0, packet, offset, ownerIdBytes.Length);
        offset += ownerIdBytes.Length;
        
        // Write type name length
        BitConverter.TryWriteBytes(packet.AsSpan(offset), typeNameBytes.Length);
        offset += 4;
        
        // Write type name
        Array.Copy(typeNameBytes, 0, packet, offset, typeNameBytes.Length);
        offset += typeNameBytes.Length;
        
        // Write payload
        Array.Copy(data, 0, packet, offset, data.Length);
        
        return packet;
    }
    
    /// <summary>
    /// Build a delete packet (contains type and string ID).
    /// </summary>
    public static byte[] BuildDeletePacket(string replicaId)
    {
        var idBytes = System.Text.Encoding.UTF8.GetBytes(replicaId);
        
        // Packet format: [1 byte TYPE][4 bytes ID length][N bytes ID]
        var packet = new byte[1 + 4 + idBytes.Length];
        var offset = 0;
        
        // Write packet type
        packet[offset] = (byte)ReplicaPacketType.Delete;
        offset += 1;
        
        // Write ID length
        BitConverter.TryWriteBytes(packet.AsSpan(offset), idBytes.Length);
        offset += 4;
        
        // Write ID
        Array.Copy(idBytes, 0, packet, offset, idBytes.Length);
        
        return packet;
    }

    /// <summary>
    /// Build a Subscribe or Unsubscribe control plane packet.
    /// </summary>
    public static byte[] BuildControlPacket(ReplicaPacketType packetType, string pathPattern)
    {
        if (packetType != ReplicaPacketType.Subscribe && packetType != ReplicaPacketType.Unsubscribe)
            throw new ArgumentException("Use BuildControlPacket only for Subscribe or Unsubscribe");
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(pathPattern);
        var packet = new byte[1 + 4 + pathBytes.Length];
        packet[0] = (byte)packetType;
        BitConverter.TryWriteBytes(packet.AsSpan(1), pathBytes.Length);
        Array.Copy(pathBytes, 0, packet, 5, pathBytes.Length);
        return packet;
    }
    
    /// <summary>
    /// Parse path pattern from Subscribe/Unsubscribe packet.
    /// </summary>
    public static bool TryParsePathPattern(byte[] data, out string pathPattern)
    {
        pathPattern = string.Empty;
        if (data.Length < 5) return false;
        var pathLength = BitConverter.ToInt32(data, 1);
        if (data.Length < 5 + pathLength) return false;
        pathPattern = System.Text.Encoding.UTF8.GetString(data, 5, pathLength);
        return true;
    }
    
    /// <summary>
    /// Packets handled by feature plugins (not <see cref="ReplicaManager"/>).
    /// <c>0x10</c> legacy JPEG; <c>0x20–0x2F</c> DualSync / media extension band.
    /// </summary>
    public static bool IsPluginHandledPacket(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return false;
        var b = data[0];
        if (b == (byte)ReplicaPacketType.VideoFrame)
            return true;
        return b is >= 0x20 and <= 0x2F;
    }

    /// <summary>
    /// Parse packet type from received data.
    /// </summary>
    public static bool TryParsePacketType(byte[] data, out ReplicaPacketType packetType)
    {
        if (data.Length < 1)
        {
            packetType = default;
            return false;
        }
        
        packetType = (ReplicaPacketType)data[0];
        return Enum.IsDefined(typeof(ReplicaPacketType), packetType);
    }
    
    /// <summary>
    /// Parse string ID from packet data.
    /// </summary>
    public static bool TryParseId(byte[] data, out string replicaId)
    {
        if (data.Length < 5) // 1 byte type + 4 bytes ID length minimum
        {
            replicaId = string.Empty;
            return false;
        }
        
        var offset = 1; // Skip packet type
        var idLength = BitConverter.ToInt32(data, offset);
        offset += 4;
        
        if (data.Length < offset + idLength)
        {
            replicaId = string.Empty;
            return false;
        }
        
        replicaId = System.Text.Encoding.UTF8.GetString(data, offset, idLength);
        return true;
    }
    
    /// <summary>
    /// Parse full packet data (for Create/Update/FullSync packets).
    /// </summary>
    public static bool TryParsePacket(byte[] data, out string replicaId, out string ownerId, out string typeName, out byte[] payload)
    {
        replicaId = string.Empty;
        ownerId = string.Empty;
        typeName = string.Empty;
        payload = Array.Empty<byte>();
        
        if (data.Length < 13) // 1 (type) + 4 (ID length) + 4 (OwnerId length) + 4 (type name length)
        {
            return false;
        }
        
        var offset = 1; // Skip packet type
        
        // Extract ID length
        var idLength = BitConverter.ToInt32(data, offset);
        offset += 4;
        
        if (data.Length < offset + idLength + 8) // Need at least ID + OwnerId length + type name length
        {
            return false;
        }
        
        // Extract ID
        replicaId = System.Text.Encoding.UTF8.GetString(data, offset, idLength);
        offset += idLength;
        
        // Extract OwnerId length
        var ownerIdLength = BitConverter.ToInt32(data, offset);
        offset += 4;
        
        if (data.Length < offset + ownerIdLength + 4) // Need at least OwnerId + type name length
        {
            return false;
        }
        
        // Extract OwnerId
        ownerId = System.Text.Encoding.UTF8.GetString(data, offset, ownerIdLength);
        offset += ownerIdLength;
        
        // Extract type name length
        var typeNameLength = BitConverter.ToInt32(data, offset);
        offset += 4;
        
        if (data.Length < offset + typeNameLength)
        {
            return false;
        }
        
        // Extract type name
        typeName = System.Text.Encoding.UTF8.GetString(data, offset, typeNameLength);
        offset += typeNameLength;
        
        // Extract payload
        payload = data[offset..];
        
        return true;
    }
}
