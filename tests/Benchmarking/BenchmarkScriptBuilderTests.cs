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
            "size=64m",
            "ioengine=libaio",
            "direct=1",
            "time_based=1",
            "runtime=12",
            "ramp_time=2",
            "bs=4k",
            "iodepth=2",
            "numjobs=1",
            "group_reporting=1",
            "invalidate=1",
            "[read]",
            "rw=read",
            "[write]",
            "rw=write",
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
        script.Should().Contain($"echo {BenchmarkScriptBuilder.FioJsonBeginMarker}");
        script.Should().Contain($"echo {BenchmarkScriptBuilder.FioJsonEndMarker}");
    }
}
