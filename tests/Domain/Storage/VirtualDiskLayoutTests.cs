using FluentAssertions;
using TeleDisk.Domain.Storage;

namespace TeleDisk.Tests.Domain.Storage;

public sealed class VirtualDiskLayoutTests
{
    [Theory]
    [InlineData(VirtualDiskLayout.ChunkSizeBytes, 4 * 1024 * 1024)]
    [InlineData(VirtualDiskLayout.CapacityBytes, 1024L * 1024 * 1024)]
    public void LayoutConstants_ShouldMatchExpectedValues(long actual, long expected)
    {
        actual.Should().Be(expected);
    }

    [Fact]
    public void MaxChunkPayload_ShouldCoverChunkSize()
    {
        VirtualDiskLayout.MaxChunkPayloadBytes.Should().BeGreaterThan(VirtualDiskLayout.ChunkSizeBytes);
    }
}
