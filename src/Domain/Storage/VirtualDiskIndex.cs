namespace TeleDisk.Domain.Storage;

internal sealed record VirtualDiskIndex(long CapacityBytes, int ChunkSizeBytes, Dictionary<long, VirtualDiskChunk> Chunks);


internal sealed record VirtualDiskChunk(string FileId, string Sha256);
