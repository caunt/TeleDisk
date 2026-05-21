using Microsoft.Extensions.Logging;
using TeleDisk.Domain.Storage;
using TeleDisk.Infrastructure.Telegram;

namespace TeleDisk.Application;

internal sealed class VirtualDiskService(TelegramBlobStore telegramBlobStore, ILogger<ChunkedVirtualDisk> logger)
{
    private readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    private ChunkedVirtualDisk? _disk;

    internal async Task ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
        await (await GetDiskAsync(cancellationToken)).ReadAsync(offset, destination, cancellationToken);

    internal async Task WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken) =>
        await (await GetDiskAsync(cancellationToken)).WriteAsync(offset, source, cancellationToken);

    internal async Task SaveAsync(CancellationToken cancellationToken) =>
        await (await GetDiskAsync(cancellationToken)).SaveAsync(cancellationToken);

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
