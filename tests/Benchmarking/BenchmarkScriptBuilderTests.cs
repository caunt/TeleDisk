using FluentAssertions;
using TeleDisk.Benchmarks;

namespace TeleDisk.Tests.Benchmarking;

public sealed class BenchmarkScriptBuilderTests
{
    [Fact]
    public void BuildFioConfig_MatchesExpectedShape()
    {
        BenchmarkScriptBuilder.BuildFioConfig().Should().Equal(
        [
            "[global]",
            "directory=/mnt/nbd",
            "filename=benchfile",
            "size=256m",
            "ioengine=libaio",
            "direct=1",
            "time_based=1",
            "runtime=25",
            "ramp_time=3",
            "bs=4k",
            "iodepth=32",
            "numjobs=1",
            "group_reporting=1",
            "invalidate=1",
            "[read]",
            "rw=randread",
            "[write]",
            "rw=randwrite",
            "stonewall"
        ]);
    }

    [Fact]
    public void BuildBenchmarkScript_UsesPrintfAndNoHeredoc()
    {
        var script = BenchmarkScriptBuilder.BuildBenchmarkScript("/tmp/tele-disk.fio", "/tmp/fio-results.json", "host.testcontainers.internal", 10809);

        script.Should().Contain("printf '%s\\n'");
        script.Should().NotContain("<<'EOF'");
        script.Should().NotContain("EOF");
        script.Should().Contain("fio --output-format=json --output=/tmp/fio-results.json /tmp/tele-disk.fio");
    }
}
