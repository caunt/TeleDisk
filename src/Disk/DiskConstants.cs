namespace TeleDisk.Disk;

internal static class DiskConstants {
    internal const int MaxFileSizeBytes = 20 * 1024 * 1024;
    internal const int ChunkSizeBytes = 4 * 1024 * 1024;
    internal const long VirtualDiskSizeBytes = 1024L * 1024 * 1024;
}
