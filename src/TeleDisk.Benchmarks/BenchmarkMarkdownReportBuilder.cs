using System.Globalization;
using System.Text;

namespace TeleDisk.Benchmarks;

internal static class BenchmarkMarkdownReportBuilder
{
    private const double BytesPerKiB = 1024;
    private const double BytesPerMiB = 1024 * 1024;

    internal static string Build(BenchmarkMetrics metrics)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## TeleDisk Telegram NBD Benchmark");
        builder.AppendLine();
        builder.AppendLine("| Metric | Value |");
        builder.AppendLine("|---|---:|");
        builder.AppendLine($"| Read Bytes | {metrics.ReadBytes.ToString("F0", CultureInfo.InvariantCulture)} B |");
        builder.AppendLine($"| Write Bytes | {metrics.WriteBytes.ToString("F0", CultureInfo.InvariantCulture)} B |");
        builder.AppendLine($"| Read Operations | {metrics.ReadOperations.ToString("F2", CultureInfo.InvariantCulture)} |");
        builder.AppendLine($"| Write Operations | {metrics.WriteOperations.ToString("F2", CultureInfo.InvariantCulture)} |");
        builder.AppendLine($"| Read Runtime (ms) | {metrics.ReadRuntimeMilliseconds.ToString("F2", CultureInfo.InvariantCulture)} |");
        builder.AppendLine($"| Write Runtime (ms) | {metrics.WriteRuntimeMilliseconds.ToString("F2", CultureInfo.InvariantCulture)} |");
        builder.AppendLine($"| Read Throughput | {FormatThroughput(metrics.ReadThroughputBytesPerSecond)} |");
        builder.AppendLine($"| Write Throughput | {FormatThroughput(metrics.WriteThroughputBytesPerSecond)} |");
        builder.AppendLine($"| Read IOPS | {metrics.ReadIops.ToString("F3", CultureInfo.InvariantCulture)} |");
        builder.AppendLine($"| Write IOPS | {metrics.WriteIops.ToString("F3", CultureInfo.InvariantCulture)} |");
        builder.AppendLine($"| Read Latency (ms) | {metrics.ReadLatencyMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} |");
        builder.AppendLine($"| Write Latency (ms) | {metrics.WriteLatencyMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} |");
        return builder.ToString();
    }

    private static string FormatThroughput(double bytesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);
        if (bytesPerSecond >= BytesPerMiB / 100)
        {
            return $"{(bytesPerSecond / BytesPerMiB).ToString("F2", CultureInfo.InvariantCulture)} MiB/s";
        }

        if (bytesPerSecond >= BytesPerKiB / 100)
        {
            return $"{(bytesPerSecond / BytesPerKiB).ToString("F2", CultureInfo.InvariantCulture)} KiB/s";
        }

        return $"{bytesPerSecond.ToString("F2", CultureInfo.InvariantCulture)} B/s";
    }
}
