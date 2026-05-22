using FluentAssertions;
using TeleDisk.Benchmarks;

namespace TeleDisk.Tests.Benchmarking;

public sealed class FioJsonExtractorTests
{
    [Fact]
    public void ExtractSingleJsonDocument_ParsesMarkerWrappedJson()
    {
        var output = string.Join(
            "\n",
            "noise before",
            BenchmarkScriptBuilder.FioJsonBeginMarker,
            "{\"jobs\":[{\"read\":{\"bw_bytes\":1}}]}",
            BenchmarkScriptBuilder.FioJsonEndMarker,
            "noise after");

        var json = FioJsonExtractor.ExtractSingleJsonDocument(
            output,
            BenchmarkScriptBuilder.FioJsonBeginMarker,
            BenchmarkScriptBuilder.FioJsonEndMarker);

        json.Should().Be("{\"jobs\":[{\"read\":{\"bw_bytes\":1}}]}");
    }

    [Fact]
    public void ExtractSingleJsonDocument_ThrowsForMissingOrDuplicateMarkers()
    {
        var valid = string.Join(
            "\n",
            BenchmarkScriptBuilder.FioJsonBeginMarker,
            "{}",
            BenchmarkScriptBuilder.FioJsonEndMarker);

        var missingBegin = $"{{}}\n{BenchmarkScriptBuilder.FioJsonEndMarker}";
        var missingEnd = $"{BenchmarkScriptBuilder.FioJsonBeginMarker}\n{{}}";
        var duplicateBegin = valid + "\n" + BenchmarkScriptBuilder.FioJsonBeginMarker;
        var duplicateEnd = valid + "\n" + BenchmarkScriptBuilder.FioJsonEndMarker;

        Action missingBeginAction = () => FioJsonExtractor.ExtractSingleJsonDocument(
            missingBegin,
            BenchmarkScriptBuilder.FioJsonBeginMarker,
            BenchmarkScriptBuilder.FioJsonEndMarker);
        Action missingEndAction = () => FioJsonExtractor.ExtractSingleJsonDocument(
            missingEnd,
            BenchmarkScriptBuilder.FioJsonBeginMarker,
            BenchmarkScriptBuilder.FioJsonEndMarker);
        Action duplicateBeginAction = () => FioJsonExtractor.ExtractSingleJsonDocument(
            duplicateBegin,
            BenchmarkScriptBuilder.FioJsonBeginMarker,
            BenchmarkScriptBuilder.FioJsonEndMarker);
        Action duplicateEndAction = () => FioJsonExtractor.ExtractSingleJsonDocument(
            duplicateEnd,
            BenchmarkScriptBuilder.FioJsonBeginMarker,
            BenchmarkScriptBuilder.FioJsonEndMarker);

        missingBeginAction.Should().Throw<InvalidOperationException>().WithMessage("Missing fio begin marker*");
        missingEndAction.Should().Throw<InvalidOperationException>().WithMessage("Missing fio end marker*");
        duplicateBeginAction.Should().Throw<InvalidOperationException>().WithMessage("Duplicate fio begin marker*");
        duplicateEndAction.Should().Throw<InvalidOperationException>().WithMessage("Duplicate fio end marker*");
    }
}
