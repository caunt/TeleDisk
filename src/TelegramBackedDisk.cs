using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TeleDisk;

internal sealed class TelegramBackedDisk(TelegramStore telegramStore, TelegramNbdIndex telegramNbdIndex, ILogger<TelegramBackedDisk> logger) {
    readonly Dictionary<long, byte[]> _chunkCache = [];
    readonly HashSet<long> _dirtyChunkIndexes = [];
    readonly SemaphoreSlim _semaphore = new(1, 1);

    internal static async Task<TelegramBackedDisk> LoadAsync(TelegramStore telegramStore, CancellationToken cancellationToken) {
        var indexFileId = await telegramStore.GetIndexFileIdAsync(cancellationToken);
        var logger = telegramStore.LoggerFactory.CreateLogger<TelegramBackedDisk>();

        if (string.IsNullOrWhiteSpace(indexFileId))
            return new TelegramBackedDisk(telegramStore, new TelegramNbdIndex(TelegramNbdConstants.VirtualDiskSizeBytes, TelegramNbdConstants.TelegramChunkSizeBytes, []), logger);

        var telegramNbdIndex = JsonSerializer.Deserialize<TelegramNbdIndex>(await telegramStore.DownloadFileAsync(indexFileId, cancellationToken));
        return new TelegramBackedDisk(telegramStore, telegramNbdIndex ?? throw new InvalidOperationException("Invalid Telegram NBD index."), logger);
    }

    internal async Task ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) {
        await _semaphore.WaitAsync(cancellationToken);

        try {
            ValidateRange(offset, destination.Length);

            for (var destinationOffset = 0; destinationOffset < destination.Length;) {
                var chunkIndex = offset / telegramNbdIndex.ChunkSizeBytes;
                var chunkOffset = (int)(offset % telegramNbdIndex.ChunkSizeBytes);
                var bytesToCopy = Math.Min(destination.Length - destinationOffset, telegramNbdIndex.ChunkSizeBytes - chunkOffset);
                var chunkBytes = await GetChunkAsync(chunkIndex, false, cancellationToken);

                if (chunkBytes is null)
                    destination.Slice(destinationOffset, bytesToCopy).Span.Clear();
                else
                    chunkBytes.AsMemory(chunkOffset, bytesToCopy).CopyTo(destination.Slice(destinationOffset, bytesToCopy));

                offset += bytesToCopy;
                destinationOffset += bytesToCopy;
            }
        }
        finally {
            _semaphore.Release();
        }
    }

    internal async Task WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken) {
        await _semaphore.WaitAsync(cancellationToken);

        try {
            ValidateRange(offset, source.Length);

            for (var sourceOffset = 0; sourceOffset < source.Length;) {
                var chunkIndex = offset / telegramNbdIndex.ChunkSizeBytes;
                var chunkOffset = (int)(offset % telegramNbdIndex.ChunkSizeBytes);
                var bytesToCopy = Math.Min(source.Length - sourceOffset, telegramNbdIndex.ChunkSizeBytes - chunkOffset);
                var chunkBytes = await GetChunkAsync(chunkIndex, true, cancellationToken) ?? throw new InvalidOperationException();

                source.Slice(sourceOffset, bytesToCopy).CopyTo(chunkBytes.AsMemory(chunkOffset, bytesToCopy));
                _dirtyChunkIndexes.Add(chunkIndex);

                offset += bytesToCopy;
                sourceOffset += bytesToCopy;
            }
        }
        finally {
            _semaphore.Release();
        }
    }

    internal async Task SaveAsync(CancellationToken cancellationToken) {
        await _semaphore.WaitAsync(cancellationToken);

        try {
            foreach (var chunkIndex in _dirtyChunkIndexes.ToArray()) {
                var chunkBytes = _chunkCache[chunkIndex];
                var fileId = await telegramStore.UploadFileAsync(chunkBytes, $"chunk-{chunkIndex}.bin", cancellationToken);
                telegramNbdIndex.Chunks[chunkIndex] = new TelegramNbdChunk(fileId, Convert.ToHexString(SHA256.HashData(chunkBytes)));
                _dirtyChunkIndexes.Remove(chunkIndex);
                logger.LogInformation("Saved chunk {ChunkIndex}", chunkIndex);
            }

            var indexFileId = await telegramStore.UploadFileAsync(JsonSerializer.SerializeToUtf8Bytes(telegramNbdIndex), "telegram-nbd-index.json", cancellationToken);
            await telegramStore.SetIndexFileIdAsync(indexFileId, cancellationToken);
            logger.LogInformation("Saved index {IndexFileId}", indexFileId);
        }
        finally {
            _semaphore.Release();
        }
    }

    async Task<byte[]?> GetChunkAsync(long chunkIndex, bool create, CancellationToken cancellationToken) {
        if (_chunkCache.TryGetValue(chunkIndex, out var chunkBytes))
            return chunkBytes;

        if (!telegramNbdIndex.Chunks.TryGetValue(chunkIndex, out var chunkInfo))
            return create ? _chunkCache[chunkIndex] = new byte[telegramNbdIndex.ChunkSizeBytes] : null;

        chunkBytes = await telegramStore.DownloadFileAsync(chunkInfo.FileId, cancellationToken);
        if (chunkBytes.Length != telegramNbdIndex.ChunkSizeBytes)
            Array.Resize(ref chunkBytes, telegramNbdIndex.ChunkSizeBytes);

        return _chunkCache[chunkIndex] = chunkBytes;
    }

    static void ValidateRange(long offset, int length) {
        if (offset < 0 || length < 0 || offset > TelegramNbdConstants.VirtualDiskSizeBytes - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }
}
