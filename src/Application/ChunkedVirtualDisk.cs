using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeleDisk.Domain.Storage;
using TeleDisk.Infrastructure.Telegram;

namespace TeleDisk.Application;

internal sealed class ChunkedVirtualDisk(TelegramBlobStore telegramBlobStore, VirtualDiskIndex diskIndex, ILogger<ChunkedVirtualDisk> logger)
{
    private readonly Dictionary<long, byte[]> _chunkCache = [];
    private readonly HashSet<long> _dirtyChunkIndexes = [];
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal static async Task<ChunkedVirtualDisk> LoadAsync(TelegramBlobStore telegramBlobStore, ILogger<ChunkedVirtualDisk> logger, CancellationToken cancellationToken)
    {
        var indexFileId = await telegramBlobStore.GetIndexFileIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(indexFileId))
        {
            return new ChunkedVirtualDisk(telegramBlobStore, new(VirtualDiskLayout.CapacityBytes, VirtualDiskLayout.ChunkSizeBytes, []), logger);
        }

        var payload = await telegramBlobStore.DownloadFileAsync(indexFileId, cancellationToken);
        var diskIndex = JsonSerializer.Deserialize<VirtualDiskIndex>(payload) ?? throw new InvalidOperationException("Invalid disk index.");
        return new ChunkedVirtualDisk(telegramBlobStore, diskIndex, logger);
    }

    internal async Task ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            ValidateRange(offset, destination.Length);
            for (var destinationOffset = 0; destinationOffset < destination.Length;)
            {
                var chunkIndex = offset / diskIndex.ChunkSizeBytes;
                var chunkOffset = (int)(offset % diskIndex.ChunkSizeBytes);
                var bytesToCopy = Math.Min(destination.Length - destinationOffset, diskIndex.ChunkSizeBytes - chunkOffset);
                var chunk = await GetChunkAsync(chunkIndex, false, cancellationToken);
                if (chunk is null)
                {
                    destination.Slice(destinationOffset, bytesToCopy).Span.Clear();
                }
                else
                {
                    chunk.AsMemory(chunkOffset, bytesToCopy).CopyTo(destination.Slice(destinationOffset, bytesToCopy));
                }

                offset += bytesToCopy;
                destinationOffset += bytesToCopy;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    internal async Task WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            ValidateRange(offset, source.Length);
            for (var sourceOffset = 0; sourceOffset < source.Length;)
            {
                var chunkIndex = offset / diskIndex.ChunkSizeBytes;
                var chunkOffset = (int)(offset % diskIndex.ChunkSizeBytes);
                var bytesToCopy = Math.Min(source.Length - sourceOffset, diskIndex.ChunkSizeBytes - chunkOffset);
                var chunk = await GetChunkAsync(chunkIndex, true, cancellationToken) ?? throw new InvalidOperationException("Chunk initialization failed.");
                source.Slice(sourceOffset, bytesToCopy).CopyTo(chunk.AsMemory(chunkOffset, bytesToCopy));
                _dirtyChunkIndexes.Add(chunkIndex);
                offset += bytesToCopy;
                sourceOffset += bytesToCopy;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    internal async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            foreach (var chunkIndex in _dirtyChunkIndexes.ToArray())
            {
                var chunk = _chunkCache[chunkIndex];
                var fileId = await telegramBlobStore.UploadFileAsync(chunk, $"chunk-{chunkIndex}.bin", cancellationToken);
                diskIndex.Chunks[chunkIndex] = new(fileId, Convert.ToHexString(SHA256.HashData(chunk)));
                _dirtyChunkIndexes.Remove(chunkIndex);
                logger.LogInformation("Saved chunk {ChunkIndex}", chunkIndex);
            }

            var indexFileId = await telegramBlobStore.UploadFileAsync(JsonSerializer.SerializeToUtf8Bytes(diskIndex), "disk-index.json", cancellationToken);
            await telegramBlobStore.SetIndexFileIdAsync(indexFileId, cancellationToken);
            logger.LogInformation("Saved index {IndexFileId}", indexFileId);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<byte[]?> GetChunkAsync(long chunkIndex, bool createChunkWhenMissing, CancellationToken cancellationToken)
    {
        if (_chunkCache.TryGetValue(chunkIndex, out var chunk))
        {
            return chunk;
        }

        if (!diskIndex.Chunks.TryGetValue(chunkIndex, out var metadata))
        {
            return createChunkWhenMissing ? _chunkCache[chunkIndex] = new byte[diskIndex.ChunkSizeBytes] : null;
        }

        chunk = await telegramBlobStore.DownloadFileAsync(metadata.FileId, cancellationToken);
        if (chunk.Length != diskIndex.ChunkSizeBytes)
        {
            Array.Resize(ref chunk, diskIndex.ChunkSizeBytes);
        }

        return _chunkCache[chunkIndex] = chunk;
    }

    private static void ValidateRange(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > VirtualDiskLayout.CapacityBytes - length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }
}
