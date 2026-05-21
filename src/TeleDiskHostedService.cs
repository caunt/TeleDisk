using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeleDisk.Disk;
using TeleDisk.Nbd;

namespace TeleDisk;

internal sealed class TeleDiskHostedService(NbdServer nbdServer, DiskService diskService, ILogger<TeleDiskHostedService> logger) : BackgroundService {
    static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    /// <summary>Runs the NBD server and periodic save loop until cancellation is requested.</summary>
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
                await diskService.SaveAsync(cancellationToken);
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
            await diskService.SaveAsync(CancellationToken.None);
        }
        catch (Exception exception) {
            logger.LogError(exception, "Shutdown save failed");
        }
    }
}
