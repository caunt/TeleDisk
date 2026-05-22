using System.Text.Json;

namespace TeleDisk.Benchmarks;

internal static class FioBenchmarkReport
{
    internal static BenchmarkMetrics ParseMetrics(string fioJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fioJson);
        using var document = JsonDocument.Parse(fioJson);
        var jobs = document.RootElement.GetProperty("jobs");
        var readContext = SelectJob(jobs, fioJson, operationName: "read");
        var writeContext = SelectJob(jobs, fioJson, operationName: "write");

        var read = readContext.Operation;
        var write = writeContext.Operation;
        var writeBytes = writeContext.GetNonNegativeDouble("io_bytes", "write bytes");
        var writeOperations = writeContext.GetNonNegativeDouble("total_ios", "write operations");
        if (writeBytes <= 0 || writeOperations <= 0)
        {
            throw writeContext.BuildInvalidMetricException("fio reported zero write work");
        }

        return new BenchmarkMetrics
        {
            ReadBytes = readContext.GetNonNegativeDouble("io_bytes", "read bytes"),
            WriteBytes = writeBytes,
            ReadOperations = readContext.GetNonNegativeDouble("total_ios", "read operations"),
            WriteOperations = writeOperations,
            ReadRuntimeMilliseconds = readContext.GetPositiveDouble("runtime", "read runtime"),
            WriteRuntimeMilliseconds = writeContext.GetPositiveDouble("runtime", "write runtime"),
            ReadThroughputBytesPerSecond = readContext.GetPositiveDouble("bw_bytes", "read throughput"),
            WriteThroughputBytesPerSecond = writeContext.GetPositiveDouble("bw_bytes", "write throughput"),
            ReadIops = readContext.GetPositiveDouble("iops", "read iops"),
            WriteIops = writeContext.GetPositiveDouble("iops", "write iops"),
            ReadLatencyMilliseconds = readContext.GetLatencyMilliseconds("read latency"),
            WriteLatencyMilliseconds = writeContext.GetLatencyMilliseconds("write latency")
        };
    }

    internal static string BuildMarkdownTable(BenchmarkMetrics metrics) =>
        BenchmarkMarkdownReportBuilder.Build(metrics);

    private static ParseContext SelectJob(JsonElement jobs, string fioJson, string operationName)
    {
        JsonElement? fallback = null;
        foreach (var job in jobs.EnumerateArray())
        {
            if (!job.TryGetProperty(operationName, out var operation))
            {
                continue;
            }

            fallback ??= job;
            var context = new ParseContext(operation, job, fioJson);
            if (context.GetNonNegativeDouble("io_bytes", $"{operationName} bytes") > 0)
            {
                return context;
            }
        }

        return fallback is { } selectedJob
            ? new ParseContext(selectedJob.GetProperty(operationName), selectedJob, fioJson)
            : throw new InvalidOperationException($"fio is missing job section: {operationName}. jobs: {jobs}");
    }

    private readonly record struct ParseContext(JsonElement Operation, JsonElement SelectedJob, string FioJson)
    {
        internal double GetPositiveDouble(string propertyName, string metricName)
        {
            var value = GetNonNegativeDouble(propertyName, metricName);
            if (value <= 0)
            {
                throw BuildInvalidMetricException($"fio reported non-positive {metricName}={value}");
            }

            return value;
        }

        internal double GetNonNegativeDouble(string propertyName, string metricName)
        {
            if (!Operation.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
            {
                throw BuildInvalidMetricException($"fio is missing numeric {metricName} ({propertyName})");
            }

            var value = property.GetDouble();
            if (value < 0)
            {
                throw BuildInvalidMetricException($"fio reported negative {metricName}={value}");
            }

            return value;
        }

        internal double GetLatencyMilliseconds(string metricName)
        {
            if (TryGetLatencyMilliseconds("lat_ns", out var latencyMilliseconds) ||
                TryGetLatencyMilliseconds("clat_ns", out latencyMilliseconds))
            {
                return latencyMilliseconds;
            }

            throw BuildInvalidMetricException($"fio is missing latency mean for {metricName}");
        }

        internal InvalidOperationException BuildInvalidMetricException(string reason) =>
            new($"{reason}. Selected fio job: {SelectedJob}. fio: {FioJson}");

        private bool TryGetLatencyMilliseconds(string propertyName, out double latencyMilliseconds)
        {
            latencyMilliseconds = 0;
            if (!Operation.TryGetProperty(propertyName, out var latencySection) ||
                !latencySection.TryGetProperty("mean", out var mean) ||
                mean.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            latencyMilliseconds = mean.GetDouble() / 1_000_000;
            return latencyMilliseconds > 0;
        }
    }
}

internal sealed record BenchmarkMetrics
{
    public double ReadBytes { get; init; }

    public double WriteBytes { get; init; }

    public double ReadOperations { get; init; }

    public double WriteOperations { get; init; }

    public double ReadRuntimeMilliseconds { get; init; }

    public double WriteRuntimeMilliseconds { get; init; }

    public double ReadThroughputBytesPerSecond { get; init; }

    public double WriteThroughputBytesPerSecond { get; init; }

    public double ReadIops { get; init; }

    public double WriteIops { get; init; }

    public double ReadLatencyMilliseconds { get; init; }

    public double WriteLatencyMilliseconds { get; init; }
}
