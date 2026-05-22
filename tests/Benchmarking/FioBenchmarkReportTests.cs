using FluentAssertions;
using TeleDisk.Benchmarks;

namespace TeleDisk.Tests.Benchmarking;

public sealed class FioBenchmarkReportTests
{
    [Fact]
    public void BuildMarkdownTable_UsesAdaptiveUnitsForSmallThroughput()
    {
        var markdown = FioBenchmarkReport.BuildMarkdownTable(new BenchmarkMetrics
        {
            ReadBytes = 4096,
            WriteBytes = 2048,
            ReadOperations = 1,
            WriteOperations = 1,
            ReadRuntimeMilliseconds = 100,
            WriteRuntimeMilliseconds = 100,
            ReadThroughputBytesPerSecond = 5,
            WriteThroughputBytesPerSecond = 300,
            ReadIops = 0.125,
            WriteIops = 0.5,
            ReadLatencyMilliseconds = 10,
            WriteLatencyMilliseconds = 11
        });

        markdown.Should().Contain("| Read Throughput | 5.00 B/s |");
        markdown.Should().Contain("| Write Throughput | 0.29 KiB/s |");
        markdown.Should().Contain("| Read IOPS | 0.125 |");
        markdown.Should().Contain("| Write IOPS | 0.500 |");
    }

    [Fact]
    public void ParseMetrics_ThrowsWhenWriteBytesOrOperationsAreZero()
    {
        var fioJson = """
                      {
                        "jobs": [
                          {
                            "jobname": "write",
                            "read": { "io_bytes": 1, "total_ios": 1, "runtime": 1, "bw_bytes": 1, "iops": 1, "lat_ns": { "mean": 1 } },
                            "write": { "io_bytes": 0, "total_ios": 1, "runtime": 1, "bw_bytes": 1, "iops": 1, "lat_ns": { "mean": 1 } }
                          }
                        ]
                      }
                      """;

        var action = () => FioBenchmarkReport.ParseMetrics(fioJson);

        action.Should().Throw<InvalidOperationException>().WithMessage("*zero write work*Selected fio job*");
    }

    [Fact]
    public void ParseMetrics_SelectsWriteSectionFromJobWithPositiveWriteIo()
    {
        var fioJson = """
                      {
                        "jobs": [
                          {
                            "jobname": "read",
                            "read": { "io_bytes": 4096, "total_ios": 1, "runtime": 1000, "bw_bytes": 4096, "iops": 1.0, "lat_ns": { "mean": 1000 } },
                            "write": { "io_bytes": 0, "total_ios": 0, "runtime": 1000, "bw_bytes": 0, "iops": 0, "lat_ns": { "mean": 1000 } }
                          },
                          {
                            "jobname": "write",
                            "read": { "io_bytes": 0, "total_ios": 0, "runtime": 1000, "bw_bytes": 0, "iops": 0, "lat_ns": { "mean": 1000 } },
                            "write": { "io_bytes": 8192, "total_ios": 2, "runtime": 1000, "bw_bytes": 1024, "iops": 0.25, "lat_ns": { "mean": 2000 } }
                          }
                        ]
                      }
                      """;

        var metrics = FioBenchmarkReport.ParseMetrics(fioJson);

        metrics.WriteBytes.Should().Be(8192);
        metrics.WriteOperations.Should().Be(2);
        metrics.WriteIops.Should().Be(0.25);
    }
}
