using Microsoft.Extensions.Logging;
using TeleDisk.Telegram;

namespace TeleDisk.Disk;

internal sealed class DiskService(TelegramStorage telegramStorage, ILogger<TelegramDisk> logger) {
    readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    TelegramDisk? _telegramDisk;

    internal async Task ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) {
        var disk = await GetDiskAsync(cancellationToken);
        await disk.ReadAsync(offset, destination, cancellationToken);
    }

    internal async Task WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken) {
        var disk = await GetDiskAsync(cancellationToken);
        await disk.WriteAsync(offset, source, cancellationToken);
    }

    internal async Task SaveAsync(CancellationToken cancellationToken) {
        var disk = await GetDiskAsync(cancellationToken);
        await disk.SaveAsync(cancellationToken);
    }

    async ValueTask<TelegramDisk> GetDiskAsync(CancellationToken cancellationToken) {
        if (_telegramDisk is not null)
            return _telegramDisk;

        await _initializationSemaphore.WaitAsync(cancellationToken);

        try {
            if (_telegramDisk is null)
                _telegramDisk = await TelegramDisk.LoadAsync(telegramStorage, logger, cancellationToken);

            return _telegramDisk;
        }
        finally {
            _initializationSemaphore.Release();
        }
    }
}
