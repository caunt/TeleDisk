using System.Text.Json;

namespace TeleDisk.Benchmarks;

internal static class FioJsonExtractor
{
    internal static string ExtractSingleJsonDocument(string output, string beginMarker, string endMarker)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(beginMarker);
        ArgumentNullException.ThrowIfNull(endMarker);

        var beginIndex = output.IndexOf(beginMarker, StringComparison.Ordinal);
        if (beginIndex < 0)
        {
            throw new InvalidOperationException($"Missing fio begin marker: {beginMarker}");
        }

        if (output.IndexOf(beginMarker, beginIndex + beginMarker.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"Duplicate fio begin marker: {beginMarker}");
        }

        var endIndex = output.IndexOf(endMarker, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            throw new InvalidOperationException($"Missing fio end marker: {endMarker}");
        }

        if (output.IndexOf(endMarker, endIndex + endMarker.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"Duplicate fio end marker: {endMarker}");
        }

        var jsonStart = beginIndex + beginMarker.Length;
        if (endIndex <= jsonStart)
        {
            throw new InvalidOperationException("fio marker order is invalid; end marker appears before begin marker content.");
        }

        var json = output[jsonStart..endIndex].Trim();
        if (json.Length == 0)
        {
            throw new InvalidOperationException("fio JSON content between markers is empty.");
        }

        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("fio JSON between markers is invalid.", exception);
        }

        return json;
    }
}
