using System.Text.Json.Serialization;

namespace TeleDisk;

internal static class TelegramNbdConstants {
    internal const string TelegramStorageChatId = "@CauntHermesBot";
    internal const string TelegramIndexDescriptionPrefix = "tg-nbd-index:";
    internal const int TelegramHostedBotApiMaxDownloadFileSizeBytes = 20 * 1024 * 1024;
    internal const int TelegramChunkSizeBytes = 4 * 1024 * 1024;
    internal const long VirtualDiskSizeBytes = 1024L * 1024 * 1024;
}


internal sealed record TelegramNbdIndex(long DiskSizeBytes, int ChunkSizeBytes, Dictionary<long, TelegramNbdChunk> Chunks);


internal sealed record TelegramNbdChunk(string FileId, string Sha256);


internal sealed record TelegramBotDescription([property: JsonPropertyName("description")] string Description);


internal sealed record TelegramApiResponse<T>([property: JsonPropertyName("ok")] bool Ok, [property: JsonPropertyName("result")] T? Result, [property: JsonPropertyName("description")] string? Description);
