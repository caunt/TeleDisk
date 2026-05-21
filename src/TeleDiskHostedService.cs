using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TeleDisk;

internal sealed class TeleDiskHostedService(NbdServer nbdServer, TelegramDiskService telegramDiskService, ILogger<TeleDiskHostedService> logger) : BackgroundService {
    static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken) {
        var periodicSaveTask = SavePeriodicallyAsync(cancellationToken);

        try {
            await nbdServer.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
        }
        finally {
            await SaveOnShutdownAsync();
            await periodicSaveTask;
        }
    }

    async Task SavePeriodicallyAsync(CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            try {
                await Task.Delay(SaveInterval, cancellationToken);
                await telegramDiskService.SaveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                return;
            }
            catch (Exception exception) {
                logger.LogError(exception, "Periodic save failed");
            }
        }
    }

    async Task SaveOnShutdownAsync() {
        try {
            await telegramDiskService.SaveAsync(CancellationToken.None);
        }
        catch (Exception exception) {
            logger.LogError(exception, "Shutdown save failed");
        }
    }
}
