using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeleDisk.Telegram;

namespace TeleDisk.Disk;

internal sealed class TelegramDisk(TelegramStorage telegramStorage, DiskIndex diskIndex, ILogger<TelegramDisk> logger) {
    readonly Dictionary<long, byte[]> _chunkCache = [];
    readonly HashSet<long> _dirtyChunkIndexes = [];
    readonly SemaphoreSlim _semaphore = new(1, 1);

    internal static async Task<TelegramDisk> LoadAsync(TelegramStorage telegramStorage, ILogger<TelegramDisk> logger, CancellationToken cancellationToken) {
        var indexFileId = await telegramStorage.GetIndexFileIdAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(indexFileId))
            return new TelegramDisk(telegramStorage, new DiskIndex(DiskConstants.VirtualDiskSizeBytes, DiskConstants.ChunkSizeBytes, []), logger);

        var diskIndex = JsonSerializer.Deserialize<DiskIndex>(await telegramStorage.DownloadFileAsync(indexFileId, cancellationToken));
        return new TelegramDisk(telegramStorage, diskIndex ?? throw new InvalidOperationException("Invalid disk index."), logger);
    }

    internal async Task ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) {
        await _semaphore.WaitAsync(cancellationToken);

        try {
            ValidateRange(offset, destination.Length);

            for (var destinationOffset = 0; destinationOffset < destination.Length;) {
                var chunkIndex = offset / diskIndex.ChunkSizeBytes;
                var chunkOffset = (int)(offset % diskIndex.ChunkSizeBytes);
                var bytesToCopy = Math.Min(destination.Length - destinationOffset, diskIndex.ChunkSizeBytes - chunkOffset);
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
                var chunkIndex = offset / diskIndex.ChunkSizeBytes;
                var chunkOffset = (int)(offset % diskIndex.ChunkSizeBytes);
                var bytesToCopy = Math.Min(source.Length - sourceOffset, diskIndex.ChunkSizeBytes - chunkOffset);
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
                var fileId = await telegramStorage.UploadFileAsync(chunkBytes, $"chunk-{chunkIndex}.bin", cancellationToken);
                diskIndex.Chunks[chunkIndex] = new DiskChunk(fileId, Convert.ToHexString(SHA256.HashData(chunkBytes)));
                _dirtyChunkIndexes.Remove(chunkIndex);
                logger.LogInformation("Saved chunk {ChunkIndex}", chunkIndex);
            }

            var indexFileId = await telegramStorage.UploadFileAsync(JsonSerializer.SerializeToUtf8Bytes(diskIndex), "disk-index.json", cancellationToken);
            await telegramStorage.SetIndexFileIdAsync(indexFileId, cancellationToken);
            logger.LogInformation("Saved index {IndexFileId}", indexFileId);
        }
        finally {
            _semaphore.Release();
        }
    }

    async Task<byte[]?> GetChunkAsync(long chunkIndex, bool create, CancellationToken cancellationToken) {
        if (_chunkCache.TryGetValue(chunkIndex, out var chunkBytes))
            return chunkBytes;

        if (!diskIndex.Chunks.TryGetValue(chunkIndex, out var chunkInfo))
            return create ? _chunkCache[chunkIndex] = new byte[diskIndex.ChunkSizeBytes] : null;

        chunkBytes = await telegramStorage.DownloadFileAsync(chunkInfo.FileId, cancellationToken);
        if (chunkBytes.Length != diskIndex.ChunkSizeBytes)
            Array.Resize(ref chunkBytes, diskIndex.ChunkSizeBytes);

        return _chunkCache[chunkIndex] = chunkBytes;
    }

    static void ValidateRange(long offset, int length) {
        if (offset < 0 || length < 0 || offset > DiskConstants.VirtualDiskSizeBytes - length)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }
}
