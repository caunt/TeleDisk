namespace TeleDisk.Benchmarks;

internal static class BenchmarkScriptBuilder
{
    internal const string FioJsonBeginMarker = "__TELEDISK_FIO_JSON_BEGIN__";

    internal const string FioJsonEndMarker = "__TELEDISK_FIO_JSON_END__";

    internal static string BuildBenchmarkScript(string fioJobPath, string resultsPath, string hostName, int nbdPort)
    {
        var escapedFioConfig = string.Join("' '", BuildFioConfig().Select(line => line.Replace("'", "'\"'\"'")));
        return string.Join(Environment.NewLine, [
            "set -euo pipefail",
            "apt-get update >/dev/null",
            "DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends kmod nbd-client fio e2fsprogs util-linux >/dev/null",
            "command -v modprobe",
            "command -v nbd-client",
            "command -v fio",
            "if [ ! -b /dev/nbd0 ]; then modprobe nbd max_part=8 || true; fi",
            "if [ ! -b /dev/nbd0 ]; then echo '/dev/nbd0 is unavailable; host kernel nbd module is not accessible from this container' >&2; exit 2; fi",
            $"nbd-client {hostName} {nbdPort} /dev/nbd0",
            "mkfs.ext4 -F /dev/nbd0 >/dev/null",
            "mkdir -p /mnt/nbd",
            "mount /dev/nbd0 /mnt/nbd",
            $"printf '%s\\n' '{escapedFioConfig}' > {fioJobPath}",
            $"fio --output-format=json --output={resultsPath} {fioJobPath}",
            "umount /mnt/nbd",
            "nbd-client -d /dev/nbd0",
            $"echo {FioJsonBeginMarker}",
            $"cat {resultsPath}",
            $"echo {FioJsonEndMarker}"
        ]);
    }

    internal static string[] BuildFioConfig() =>
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
    ];
}
