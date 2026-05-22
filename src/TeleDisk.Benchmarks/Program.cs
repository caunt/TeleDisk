using System.Globalization;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TeleDisk;
using TeleDisk.Benchmarks;

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
    const string fioJobPath = "/tmp/tele-disk.fio";
    var script = BenchmarkScriptBuilder.BuildBenchmarkScript(fioJobPath, resultsPath, hostName, nbdPort);

    await using var container = new ContainerBuilder("ubuntu:latest")
        .WithPrivileged(true)
        .WithAutoRemove(false)
        .WithExtraHost(hostName, "host-gateway")
        .WithEntrypoint("bash", "-lc")
        .WithCommand(script)
        .Build();

    await container.StartAsync(cancellationToken);
    var exitCode = await container.GetExitCodeAsync(cancellationToken);
    var (stdout, stderr) = await container.GetLogsAsync(
        DateTime.MinValue,
        DateTime.MaxValue,
        false,
        cancellationToken);
    if (exitCode != 0)
    {
        throw new InvalidOperationException($"Benchmark container failed with exit code {exitCode}.{Environment.NewLine}{CaptureDiagnosticTail(stdout, stderr)}");
    }

    if (!string.IsNullOrWhiteSpace(stderr))
    {
        Console.WriteLine($"Benchmark container stderr (tail):{Environment.NewLine}{CaptureDiagnosticTail(string.Empty, stderr)}");
    }

    string fioJson;
    try
    {
        fioJson = FioJsonExtractor.ExtractSingleJsonDocument(
            stdout,
            BenchmarkScriptBuilder.FioJsonBeginMarker,
            BenchmarkScriptBuilder.FioJsonEndMarker);
    }
    catch (InvalidOperationException exception)
    {
        throw new InvalidOperationException($"Failed to capture fio JSON from container output. {exception.Message}{Environment.NewLine}{CaptureDiagnosticTail(stdout, stderr)}", exception);
    }

    using var fioDocument = JsonDocument.Parse(fioJson);
    var jobs = fioDocument.RootElement.GetProperty("jobs");
    var read = FindMetricsSection(jobs, "read");
    var write = FindMetricsSection(jobs, "write");

    var readThroughputMiBPerSecond = GetPositiveDouble(read, "bw_bytes", "read throughput", fioJson) / 1024 / 1024;
    var writeThroughputMiBPerSecond = GetPositiveDouble(write, "bw_bytes", "write throughput", fioJson) / 1024 / 1024;
    var readIops = GetPositiveDouble(read, "iops", "read iops", fioJson);
    var writeIops = GetPositiveDouble(write, "iops", "write iops", fioJson);

    return new BenchmarkMetrics
    {
        ReadThroughputMiBPerSecond = readThroughputMiBPerSecond,
        WriteThroughputMiBPerSecond = writeThroughputMiBPerSecond,
        ReadIops = readIops,
        WriteIops = writeIops,
        ReadLatencyMilliseconds = GetLatencyMilliseconds(read, "read latency", fioJson),
        WriteLatencyMilliseconds = GetLatencyMilliseconds(write, "write latency", fioJson)
    };
}

static JsonElement FindMetricsSection(JsonElement jobs, string sectionName)
{
    foreach (var job in jobs.EnumerateArray())
    {
        if (!job.TryGetProperty(sectionName, out var section))
        {
            continue;
        }

        if (section.TryGetProperty("io_bytes", out var ioBytes) &&
            ioBytes.ValueKind == JsonValueKind.Number &&
            ioBytes.TryGetInt64(out var value) &&
            value > 0)
        {
            return section;
        }
    }

    throw new InvalidOperationException($"fio did not report any {sectionName} io_bytes in jobs: {jobs}");
}

static double GetPositiveDouble(JsonElement section, string propertyName, string metricName, string fioJson)
{
    if (!section.TryGetProperty(propertyName, out var property) ||
        property.ValueKind != JsonValueKind.Number)
    {
        throw new InvalidOperationException($"fio is missing numeric {metricName} ({propertyName}). Section: {section}");
    }

    var value = property.GetDouble();
    if (value <= 0)
    {
        throw new InvalidOperationException($"fio reported non-positive {metricName}={value}. Relevant section: {section}. fio: {fioJson}");
    }

    return value;
}

static double GetLatencyMilliseconds(JsonElement section, string metricName, string fioJson)
{
    if (TryGetLatencyMilliseconds(section, "lat_ns", out var latency) ||
        TryGetLatencyMilliseconds(section, "clat_ns", out latency))
    {
        return latency;
    }

    throw new InvalidOperationException($"fio is missing latency mean for {metricName}. Section: {section}. fio: {fioJson}");
}

static bool TryGetLatencyMilliseconds(JsonElement section, string propertyName, out double latencyMilliseconds)
{
    latencyMilliseconds = 0;
    if (!section.TryGetProperty(propertyName, out var latencySection) ||
        !latencySection.TryGetProperty("mean", out var mean) ||
        mean.ValueKind != JsonValueKind.Number)
    {
        return false;
    }

    latencyMilliseconds = mean.GetDouble() / 1_000_000;
    return latencyMilliseconds > 0;
}

static string CaptureDiagnosticTail(string stdout, string stderr)
{
    const int maxLength = 4000;
    var combined = string.Concat(
        "STDOUT tail:",
        Environment.NewLine,
        TakeTail(stdout, maxLength / 2),
        Environment.NewLine,
        "STDERR tail:",
        Environment.NewLine,
        TakeTail(stderr, maxLength / 2));
    return TakeTail(combined, maxLength);
}

static string TakeTail(string text, int maxLength) =>
    text.Length <= maxLength ? text : text[^maxLength..];

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
