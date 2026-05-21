namespace TeleDisk.Disk;

internal sealed record DiskIndex(long DiskSizeBytes, int ChunkSizeBytes, Dictionary<long, DiskChunk> Chunks);


internal sealed record DiskChunk(string FileId, string Sha256);
