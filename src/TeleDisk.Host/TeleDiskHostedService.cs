using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeleDisk.Infrastructure.Telegram;
using TeleDisk.Transport.Nbd;

namespace TeleDisk;

internal sealed class TeleDiskHostedService(NbdEndpoint nbdEndpoint, ExportRegistry exportRegistry, ILogger<TeleDiskHostedService> logger) : BackgroundService
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
                await SaveAllAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (TelegramRateLimitException exception)
            {
                logger.LogDebug("Periodic save deferred due to Telegram retry_after={RetryAfterSeconds}s", exception.RetryAfter.TotalSeconds);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Periodic save failed");
            }
        }
    }

    private Task SaveAllAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(exportRegistry.GetExportNames().Select(exportName => exportRegistry.Resolve(exportName).SaveAsync(cancellationToken)));

    private async Task SaveOnShutdownAsync()
    {
        try
        {
            await SaveAllAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Shutdown save failed");
        }
    }
}
