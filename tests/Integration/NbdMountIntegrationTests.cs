using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TeleDisk;
using Xunit;

namespace TeleDisk.Tests.Integration;

public sealed class NbdMountIntegrationTests
{
    private const string BotTokenVariable = "TELEGRAM_BOT_TOKEN";
    private const string MountPath = "/mnt/nbd";
    private const string MarkerFile = "teledisk-smoke.txt";
    private const string MarkerContent = "teledisk";

    [Fact]
    public async Task NbdExport_ShouldSupportMountAndBasicFileOperations()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BotTokenVariable)))
        {
            return;
        }

        var hostApplicationBuilder = Host.CreateApplicationBuilder();
        hostApplicationBuilder.Services.AddTeleDisk();
        using var host = hostApplicationBuilder.Build();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await host.StartAsync(cancellationTokenSource.Token);

        try
        {
            var script = string.Join(";", [
                "set -euo pipefail",
                "apt-get update >/dev/null",
                "apt-get install -y nbd-client e2fsprogs >/dev/null",
                "modprobe nbd max_part=8 || true",
                "nbd-client -d /dev/nbd0 2>/dev/null || true",
                "mkdir -p /mnt/nbd",
                "nbd-client host.testcontainers.internal 10809 /dev/nbd0",
                "mkfs.ext4 -F /dev/nbd0 >/dev/null",
                "mount -t ext4 /dev/nbd0 /mnt/nbd",
                $"printf '{MarkerContent}' > {MountPath}/{MarkerFile}",
                $"cat {MountPath}/{MarkerFile} | grep -Fx '{MarkerContent}'",
                $"test -f {MountPath}/{MarkerFile}",
                "cd /",
                "umount /mnt/nbd",
                "nbd-client -d /dev/nbd0"
            ]);

            await using var container = new ContainerBuilder()
                .WithImage("ubuntu:latest")
                .WithPrivileged(true)
                .WithAutoRemove(true)
                .WithExtraHost("host.testcontainers.internal", "host-gateway")
                .WithEntrypoint("bash", "-lc")
                .WithCommand(script)
                .Build();

            await container.StartAsync(cancellationTokenSource.Token);
            var exitCode = await container.GetExitCodeAsync(cancellationTokenSource.Token);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            await host.StopAsync(cancellationTokenSource.Token);
        }
    }
}
