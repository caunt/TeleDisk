namespace TeleDisk.Domain.Storage;

internal sealed record VirtualDiskIndex(long CapacityBytes, int ChunkSizeBytes, Dictionary<long, VirtualDiskChunk> Chunks)
{
    internal static VirtualDiskIndex CreateDefault() => new(VirtualDiskLayout.CapacityBytes, VirtualDiskLayout.ChunkSizeBytes, []);

    internal static VirtualDiskIndex Sanitize(VirtualDiskIndex? persisted)
    {
        if (persisted is null)
        {
            return CreateDefault();
        }

        var maxChunkIndex = (VirtualDiskLayout.CapacityBytes - 1) / VirtualDiskLayout.ChunkSizeBytes;
        var chunks = persisted.Chunks
            .Where(static pair => pair.Key >= 0)
            .Where(pair => pair.Key <= maxChunkIndex)
            .Where(static pair => pair.Value is { FileId: { Length: > 0 } })
            .ToDictionary();

        return new(VirtualDiskLayout.CapacityBytes, VirtualDiskLayout.ChunkSizeBytes, chunks);
    }
}


internal sealed record VirtualDiskChunk(string? FileId, string? Sha256)
{
    internal bool IsZero => string.IsNullOrWhiteSpace(FileId);

    internal static VirtualDiskChunk Zero { get; } = new(null, null);
}
