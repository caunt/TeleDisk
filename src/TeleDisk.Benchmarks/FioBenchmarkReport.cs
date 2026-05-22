using System.Text.Json;

namespace TeleDisk.Benchmarks;

internal static class FioBenchmarkReport
{
    internal static BenchmarkMetrics ParseMetrics(string fioJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fioJson);
        using var document = JsonDocument.Parse(fioJson);
        var jobs = document.RootElement.GetProperty("jobs");
        var readJob = SelectJob(jobs, operationName: "read");
        var writeJob = SelectJob(jobs, operationName: "write");

        var read = readJob.GetProperty("read");
        var write = writeJob.GetProperty("write");
        var writeBytes = GetNonNegativeDouble(write, "io_bytes", "write bytes", fioJson, writeJob);
        var writeOperations = GetNonNegativeDouble(write, "total_ios", "write operations", fioJson, writeJob);
        if (writeBytes <= 0 || writeOperations <= 0)
        {
            throw BuildInvalidMetricException("fio reported zero write work", fioJson, writeJob);
        }

        return new BenchmarkMetrics
        {
            ReadBytes = GetNonNegativeDouble(read, "io_bytes", "read bytes", fioJson, readJob),
            WriteBytes = writeBytes,
            ReadOperations = GetNonNegativeDouble(read, "total_ios", "read operations", fioJson, readJob),
            WriteOperations = writeOperations,
            ReadRuntimeMilliseconds = GetPositiveDouble(read, "runtime", "read runtime", fioJson, readJob),
            WriteRuntimeMilliseconds = GetPositiveDouble(write, "runtime", "write runtime", fioJson, writeJob),
            ReadThroughputBytesPerSecond = GetPositiveDouble(read, "bw_bytes", "read throughput", fioJson, readJob),
            WriteThroughputBytesPerSecond = GetPositiveDouble(write, "bw_bytes", "write throughput", fioJson, writeJob),
            ReadIops = GetPositiveDouble(read, "iops", "read iops", fioJson, readJob),
            WriteIops = GetPositiveDouble(write, "iops", "write iops", fioJson, writeJob),
            ReadLatencyMilliseconds = GetLatencyMilliseconds(read, "read latency", fioJson, readJob),
            WriteLatencyMilliseconds = GetLatencyMilliseconds(write, "write latency", fioJson, writeJob)
        };
    }

    internal static string BuildMarkdownTable(BenchmarkMetrics metrics) =>
        BenchmarkMarkdownReportBuilder.Build(metrics);

    private static JsonElement SelectJob(JsonElement jobs, string operationName)
    {
        JsonElement? fallback = null;
        foreach (var job in jobs.EnumerateArray())
        {
            if (!job.TryGetProperty(operationName, out var operation))
            {
                continue;
            }

            fallback ??= job;
            var ioBytes = GetNonNegativeDouble(operation, "io_bytes", $"{operationName} bytes", jobs.ToString(), job);
            if (ioBytes > 0)
            {
                return job;
            }
        }

        return fallback ?? throw new InvalidOperationException($"fio is missing job section: {operationName}. jobs: {jobs}");
    }

    private static double GetPositiveDouble(JsonElement section, string propertyName, string metricName, string fioJson, JsonElement selectedJob)
    {
        var value = GetNonNegativeDouble(section, propertyName, metricName, fioJson, selectedJob);
        if (value <= 0)
        {
            throw BuildInvalidMetricException($"fio reported non-positive {metricName}={value}", fioJson, selectedJob);
        }

        return value;
    }

    private static double GetNonNegativeDouble(JsonElement section, string propertyName, string metricName, string fioJson, JsonElement selectedJob)
    {
        if (!section.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            throw BuildInvalidMetricException($"fio is missing numeric {metricName} ({propertyName})", fioJson, selectedJob);
        }

        var value = property.GetDouble();
        if (value < 0)
        {
            throw BuildInvalidMetricException($"fio reported negative {metricName}={value}", fioJson, selectedJob);
        }

        return value;
    }

    private static double GetLatencyMilliseconds(JsonElement section, string metricName, string fioJson, JsonElement selectedJob)
    {
        if (TryGetLatencyMilliseconds(section, "lat_ns", out var latencyMilliseconds) ||
            TryGetLatencyMilliseconds(section, "clat_ns", out latencyMilliseconds))
        {
            return latencyMilliseconds;
        }

        throw BuildInvalidMetricException($"fio is missing latency mean for {metricName}", fioJson, selectedJob);
    }

    private static bool TryGetLatencyMilliseconds(JsonElement section, string propertyName, out double latencyMilliseconds)
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

    private static InvalidOperationException BuildInvalidMetricException(string reason, string fioJson, JsonElement selectedJob) =>
        new($"{reason}. Selected fio job: {selectedJob}. fio: {fioJson}");
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
