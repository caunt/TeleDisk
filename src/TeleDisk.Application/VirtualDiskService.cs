using Microsoft.Extensions.Logging;
using TeleDisk.Domain.Storage;
using TeleDisk.Infrastructure.Telegram;

namespace TeleDisk.Application;

internal sealed class VirtualDiskService(TelegramBlobStore telegramBlobStore, ILogger<ChunkedVirtualDisk> logger)
{
    private static readonly TimeSpan SaveDebounceWindow = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
    private ChunkedVirtualDisk? _disk;
    private DateTimeOffset _nextAllowedSaveAt = DateTimeOffset.MinValue;

    internal async Task ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var disk = await GetDiskAsync(cancellationToken);
        await disk.ReadAsync(offset, destination, cancellationToken);
    }

    internal async Task WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        var disk = await GetDiskAsync(cancellationToken);
        await disk.WriteAsync(offset, source, cancellationToken);
    }

    internal async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow < _nextAllowedSaveAt)
            {
                return;
            }

            var disk = await GetDiskAsync(cancellationToken);
            await disk.SaveAsync(cancellationToken);
            _nextAllowedSaveAt = DateTimeOffset.UtcNow + SaveDebounceWindow;
        }
        catch (TelegramRateLimitException exception)
        {
            _nextAllowedSaveAt = DateTimeOffset.UtcNow + exception.RetryAfter;
            throw;
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    internal async Task WriteZeroesAsync(long offset, int length, CancellationToken cancellationToken)
    {
        var disk = await GetDiskAsync(cancellationToken);
        await disk.WriteZeroesAsync(offset, length, cancellationToken);
    }

    internal async ValueTask<bool> IsAllocatedAsync(long offset, CancellationToken cancellationToken)
    {
        var disk = await GetDiskAsync(cancellationToken);
        return disk.IsAllocated(offset);
    }

    private async ValueTask<ChunkedVirtualDisk> GetDiskAsync(CancellationToken cancellationToken)
    {
        if (_disk is not null)
        {
            return _disk;
        }

        await _initializationSemaphore.WaitAsync(cancellationToken);
        try
        {
            _disk ??= await ChunkedVirtualDisk.LoadAsync(telegramBlobStore, logger, cancellationToken);
            return _disk;
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }
}
