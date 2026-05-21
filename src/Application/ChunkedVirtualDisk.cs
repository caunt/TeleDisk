using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeleDisk.Domain.Storage;
using TeleDisk.Infrastructure.Telegram;

namespace TeleDisk.Application;

internal sealed class ChunkedVirtualDisk(TelegramBlobStore telegramBlobStore, VirtualDiskIndex diskIndex, ILogger<ChunkedVirtualDisk> logger)
{
    private readonly HashSet<long> _dirtyChunkIndexes = [];
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal static async Task<ChunkedVirtualDisk> LoadAsync(TelegramBlobStore telegramBlobStore, ILogger<ChunkedVirtualDisk> logger, CancellationToken cancellationToken)
    {
        var indexFileId = await telegramBlobStore.GetIndexFileIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(indexFileId))
        {
            return new ChunkedVirtualDisk(telegramBlobStore, new(VirtualDiskLayout.CapacityBytes, VirtualDiskLayout.ChunkSizeBytes, []), logger);
        }

        var diskIndex = await telegramBlobStore.DownloadJsonAsync<VirtualDiskIndex>(indexFileId, cancellationToken) ?? throw new InvalidOperationException("Invalid disk index.");
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
                if (!diskIndex.Chunks.TryGetValue(chunkIndex, out var metadata))
                {
                    destination.Slice(destinationOffset, bytesToCopy).Span.Clear();
                }
                else
                {
                    await telegramBlobStore.DownloadRangeAsync(metadata.FileId, chunkOffset, destination.Slice(destinationOffset, bytesToCopy), cancellationToken);
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
                var chunkPayload = await telegramBlobStore.DownloadChunkOrZeroAsync(diskIndex.Chunks.GetValueOrDefault(chunkIndex)?.FileId, diskIndex.ChunkSizeBytes, cancellationToken);
                source.Slice(sourceOffset, bytesToCopy).Span.CopyTo(chunkPayload.AsSpan(chunkOffset, bytesToCopy));
                var fileId = await telegramBlobStore.UploadFileAsync(chunkPayload, $"chunk-{chunkIndex}.bin", cancellationToken);
                diskIndex.Chunks[chunkIndex] = new(fileId, Convert.ToHexString(SHA256.HashData(chunkPayload)));
                _dirtyChunkIndexes.Add(chunkIndex);
                logger.LogInformation("Saved chunk {ChunkIndex}", chunkIndex);
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
            if (_dirtyChunkIndexes.Count == 0)
            {
                return;
            }

            var indexFileId = await telegramBlobStore.UploadJsonAsync(diskIndex, "disk-index.json", cancellationToken);
            await telegramBlobStore.SetIndexFileIdAsync(indexFileId, cancellationToken);
            _dirtyChunkIndexes.Clear();
            logger.LogInformation("Saved index {IndexFileId}", indexFileId);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static void ValidateRange(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > VirtualDiskLayout.CapacityBytes - length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }
}
