namespace TeleDisk;

internal sealed class TelegramDiskService(TelegramStore telegramStore) {
    readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    TelegramBackedDisk? _telegramBackedDisk;

    internal async Task ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) {
        var telegramBackedDisk = await GetDiskAsync(cancellationToken);
        await telegramBackedDisk.ReadAsync(offset, destination, cancellationToken);
    }

    internal async Task WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken cancellationToken) {
        var telegramBackedDisk = await GetDiskAsync(cancellationToken);
        await telegramBackedDisk.WriteAsync(offset, source, cancellationToken);
    }

    internal async Task SaveAsync(CancellationToken cancellationToken) {
        var telegramBackedDisk = await GetDiskAsync(cancellationToken);
        await telegramBackedDisk.SaveAsync(cancellationToken);
    }

    async ValueTask<TelegramBackedDisk> GetDiskAsync(CancellationToken cancellationToken) {
        if (_telegramBackedDisk is not null)
            return _telegramBackedDisk;

        await _initializationSemaphore.WaitAsync(cancellationToken);

        try {
            if (_telegramBackedDisk is null)
                _telegramBackedDisk = await TelegramBackedDisk.LoadAsync(telegramStore, cancellationToken);

            return _telegramBackedDisk;
        }
        finally {
            _initializationSemaphore.Release();
        }
    }
}
