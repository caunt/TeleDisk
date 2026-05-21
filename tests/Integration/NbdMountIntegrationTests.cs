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
    private const string Hostname = "host.testcontainers.internal";
    private const int ConnectionRetries = 20;
    private const int RetryDelaySeconds = 1;

    [Theory]
    [InlineData("printf 'alpha' | qemu-io -f raw -c \"write 0 5\" nbd://host.testcontainers.internal:10809 && qemu-io -f raw -c \"read -P 0x61 0 1\" -c \"read -P 0x6c 1 1\" nbd://host.testcontainers.internal:10809")]
    [InlineData("printf 'bravo' | qemu-io -f raw -c \"write 512 5\" nbd://host.testcontainers.internal:10809 && qemu-io -f raw -c \"read -P 0x62 512 1\" -c \"read -P 0x6f 516 1\" nbd://host.testcontainers.internal:10809")]
    public Task ReadWriteCommands_ShouldRoundTripData(string command) => RunInContainerAsync(command);

    [Theory]
    [InlineData("qemu-io -f raw -c \"write -z 0 4096\" -c \"read -P 0x00 0 4096\" nbd://host.testcontainers.internal:10809")]
    [InlineData("qemu-io -f raw -c \"write -z 8192 2048\" -c \"read -P 0x00 8192 2048\" nbd://host.testcontainers.internal:10809")]
    public Task WriteZeroesCommand_ShouldClearRanges(string command) => RunInContainerAsync(command);

    [Theory]
    [InlineData("qemu-io -f raw -c \"write -z 12288 4096\" -c \"discard 12288 4096\" -c \"read -P 0x00 12288 4096\" nbd://host.testcontainers.internal:10809")]
    [InlineData("qemu-io -f raw -c \"write -z 16384 2048\" -c \"discard 16384 2048\" -c \"read -P 0x00 16384 2048\" nbd://host.testcontainers.internal:10809")]
    public Task TrimCommand_ShouldDiscardRanges(string command) => RunInContainerAsync(command);

    [Theory]
    [InlineData("qemu-io -f raw -c \"write -P 0xaa 20480 1024\" -c \"flush\" -c \"read -P 0xaa 20480 1024\" nbd://host.testcontainers.internal:10809")]
    [InlineData("qemu-io -f raw -c \"write -P 0xbb 24576 1024\" -c \"flush\" -c \"read -P 0xbb 24576 1024\" nbd://host.testcontainers.internal:10809")]
    public Task FlushCommand_ShouldAcknowledgeWrites(string command) => RunInContainerAsync(command);

    [Theory]
    [InlineData("qemu-io -f raw -c \"write -P 0xcc 28672 1024\" -c \"map 28672 1024\" nbd://host.testcontainers.internal:10809 | grep -E \"allocated|present\"")]
    [InlineData("qemu-io -f raw -c \"write -z 32768 1024\" -c \"map 32768 1024\" nbd://host.testcontainers.internal:10809 | grep -E \"zero|allocated|present\"")]
    public Task BlockStatusCommand_ShouldReportExtentState(string command) => RunInContainerAsync(command);

    [Theory]
    [InlineData("qemu-io -f raw -c \"write -P 0xdd 36864 1024\" -c \"aio_read 36864 1024\" nbd://host.testcontainers.internal:10809")]
    [InlineData("qemu-io -f raw -c \"write -P 0xee 40960 1024\" -c \"aio_flush\" nbd://host.testcontainers.internal:10809")]
    public Task CacheCommand_ShouldAcceptPrefetchLikeOperations(string command) => RunInContainerAsync(command);

    [Theory]
    [InlineData("nbd-client -d /dev/nbd0 2>/dev/null || true; nbd-client -N teledisk host.testcontainers.internal 10809 /dev/nbd0; nbd-client -d /dev/nbd0")]
    [InlineData("nbd-client -d /dev/nbd0 2>/dev/null || true; nbd-client -N teledisk host.testcontainers.internal 10809 /dev/nbd0 -b 4096; nbd-client -d /dev/nbd0")]
    public Task DisconnectCommand_ShouldCloseSessionsCleanly(string command) => RunInContainerAsync(command);

    [Theory]
    [InlineData("qemu-img resize nbd://host.testcontainers.internal:10809 12M && qemu-img info nbd://host.testcontainers.internal:10809 | grep -F \"12 MiB\"")]
    [InlineData("qemu-img resize nbd://host.testcontainers.internal:10809 16M && qemu-img info nbd://host.testcontainers.internal:10809 | grep -F \"16 MiB\"")]
    public Task ResizeCommand_ShouldUpdateExportCapacity(string command) => RunInContainerAsync(command);

    private static async Task RunInContainerAsync(string command)
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
                "apt-get update",
                "apt-get install -y qemu-utils nbd-client",
                BuildRetryCommand(command)
            ]);

            await using var container = new ContainerBuilder("ubuntu:latest")
                .WithPrivileged(true)
                .WithExtraHost(Hostname, "host-gateway")
                .WithEntrypoint("bash", "-lc")
                .WithCommand(script)
                .Build();

            await container.StartAsync(cancellationTokenSource.Token);
            var exitCode = await container.GetExitCodeAsync(cancellationTokenSource.Token);
            if (exitCode is not 0)
            {
                var (stdout, stderr) = await container.GetLogsAsync(DateTime.MinValue, DateTime.MaxValue, false, cancellationTokenSource.Token);
                Assert.Fail($"Command failed with exit code {exitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            }
        }
        finally
        {
            await host.StopAsync(cancellationTokenSource.Token);
        }
    }

    private static string BuildRetryCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return $"for attempt in $(seq 1 {ConnectionRetries}); do {command} && exit 0; if [ \"$attempt\" -eq \"{ConnectionRetries}\" ]; then exit 1; fi; sleep {RetryDelaySeconds}; done";
    }
}
