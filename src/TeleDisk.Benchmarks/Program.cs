using System.Globalization;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TeleDisk;

const string botTokenVariable = "TELEGRAM_BOT_TOKEN";
const string hostName = "host.testcontainers.internal";
const int nbdPort = 10809;

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(botTokenVariable)))
{
    Console.WriteLine($"{botTokenVariable} is not set; skipping benchmark.");
    return;
}

var hostApplicationBuilder = Host.CreateApplicationBuilder(args);
hostApplicationBuilder.Services.AddTeleDisk();
using var host = hostApplicationBuilder.Build();
using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(8));
await host.StartAsync(cancellationTokenSource.Token);

try
{
    var metrics = await RunContainerBenchmarkAsync(cancellationTokenSource.Token);
    var markdown = BuildMarkdownTable(metrics);
    Console.WriteLine(markdown);

    var stepSummaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (!string.IsNullOrWhiteSpace(stepSummaryPath))
    {
        await File.AppendAllTextAsync(stepSummaryPath, markdown + Environment.NewLine, cancellationTokenSource.Token);
    }
}
finally
{
    await host.StopAsync(cancellationTokenSource.Token);
}

return;

static async Task<BenchmarkMetrics> RunContainerBenchmarkAsync(CancellationToken cancellationToken)
{
    const string resultsPath = "/tmp/fio-results.json";
    var script = string.Join(";", [
        "set -euo pipefail",
        "apt-get update >/dev/null",
        "apt-get install -y nbd-client fio e2fsprogs util-linux >/dev/null",
        "modprobe nbd max_part=8",
        $"nbd-client {hostName} {nbdPort} /dev/nbd0",
        "mkfs.ext4 -F /dev/nbd0 >/dev/null",
        "mkdir -p /mnt/nbd",
        "mount /dev/nbd0 /mnt/nbd",
        $"fio --name=tele-disk --directory=/mnt/nbd --filename=benchfile --size=64m --rw=randrw --rwmixread=50 --bs=4k --iodepth=32 --numjobs=1 --direct=1 --time_based=1 --runtime=30 --group_reporting=1 --output-format=json --output={resultsPath}",
        "umount /mnt/nbd",
        "nbd-client -d /dev/nbd0",
        $"cat {resultsPath}"
    ]);

    await using var container = new ContainerBuilder()
        .WithImage("ubuntu:latest")
        .WithPrivileged(true)
        .WithAutoRemove(true)
        .WithExtraHost(hostName, "host-gateway")
        .WithEntrypoint("bash", "-lc")
        .WithCommand(script)
        .Build();

    await container.StartAsync(cancellationToken);
    var exitCode = await container.GetExitCodeAsync(cancellationToken);
    var output = await container.GetLogsAsync();
    if (exitCode != 0)
    {
        throw new InvalidOperationException($"Benchmark container failed with exit code {exitCode}.{Environment.NewLine}{output.Stderr}");
    }

    var start = output.Stdout.IndexOf('{');
    if (start < 0)
    {
        throw new InvalidOperationException("fio JSON results were not found in container output.");
    }

    var fioJson = output.Stdout[start..];
    using var fioDocument = JsonDocument.Parse(fioJson);
    var job = fioDocument.RootElement.GetProperty("jobs")[0];
    var read = job.GetProperty("read");
    var write = job.GetProperty("write");
    return new BenchmarkMetrics
    {
        ReadThroughputMiBPerSecond = read.GetProperty("bw_bytes").GetDouble() / 1024 / 1024,
        WriteThroughputMiBPerSecond = write.GetProperty("bw_bytes").GetDouble() / 1024 / 1024,
        ReadIops = read.GetProperty("iops").GetDouble(),
        WriteIops = write.GetProperty("iops").GetDouble(),
        ReadLatencyMilliseconds = read.GetProperty("lat_ns").GetProperty("mean").GetDouble() / 1_000_000,
        WriteLatencyMilliseconds = write.GetProperty("lat_ns").GetProperty("mean").GetDouble() / 1_000_000
    };
}

static string BuildMarkdownTable(BenchmarkMetrics metrics)
{
    var builder = new StringBuilder();
    builder.AppendLine("## TeleDisk Telegram NBD Benchmark");
    builder.AppendLine();
    builder.AppendLine("| Metric | Value |");
    builder.AppendLine("|---|---:|");
    builder.AppendLine($"| Read Throughput (MiB/s) | {metrics.ReadThroughputMiBPerSecond.ToString("F2", CultureInfo.InvariantCulture)} |");
    builder.AppendLine($"| Write Throughput (MiB/s) | {metrics.WriteThroughputMiBPerSecond.ToString("F2", CultureInfo.InvariantCulture)} |");
    builder.AppendLine($"| Read IOPS | {metrics.ReadIops.ToString("F0", CultureInfo.InvariantCulture)} |");
    builder.AppendLine($"| Write IOPS | {metrics.WriteIops.ToString("F0", CultureInfo.InvariantCulture)} |");
    builder.AppendLine($"| Read Latency (ms) | {metrics.ReadLatencyMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} |");
    builder.AppendLine($"| Write Latency (ms) | {metrics.WriteLatencyMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} |");
    return builder.ToString();
}

file sealed class BenchmarkMetrics
{
    public double ReadThroughputMiBPerSecond { get; init; }

    public double WriteThroughputMiBPerSecond { get; init; }

    public double ReadIops { get; init; }

    public double WriteIops { get; init; }

    public double ReadLatencyMilliseconds { get; init; }

    public double WriteLatencyMilliseconds { get; init; }
}
