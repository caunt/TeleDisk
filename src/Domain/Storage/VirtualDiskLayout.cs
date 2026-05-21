namespace TeleDisk.Domain.Storage;

internal static class VirtualDiskLayout
{
    internal const int MaxChunkPayloadBytes = 20 * 1024 * 1024;
    internal const int ChunkSizeBytes = 4 * 1024 * 1024;
    internal const long CapacityBytes = 1024L * 1024 * 1024;
}
