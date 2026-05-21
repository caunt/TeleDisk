using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeleDisk.Application;
using TeleDisk.Transport.Nbd;

namespace TeleDisk;

internal sealed class TeleDiskHostedService(NbdEndpoint nbdEndpoint, VirtualDiskService virtualDiskService, ILogger<TeleDiskHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    /// <summary>Runs the NBD server and periodic save loop until cancellation is requested.</summary>
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var periodicSaveTask = SavePeriodicallyAsync(cancellationToken);
        try
        {
            await nbdEndpoint.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await SaveOnShutdownAsync();
            await periodicSaveTask;
        }
    }

    private async Task SavePeriodicallyAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SaveInterval, cancellationToken);
                await virtualDiskService.SaveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Periodic save failed");
            }
        }
    }

    private async Task SaveOnShutdownAsync()
    {
        try
        {
            await virtualDiskService.SaveAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Shutdown save failed");
        }
    }
}
