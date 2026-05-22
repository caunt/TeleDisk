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
    var markdown = FioBenchmarkReport.BuildMarkdownTable(metrics);
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

    return FioBenchmarkReport.ParseMetrics(fioJson);
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
