using FluentAssertions;
using TeleDisk.Domain.Storage;

namespace TeleDisk.Tests.Domain.Storage;

public sealed class VirtualDiskLayoutTests
{
    [Fact]
    public void ChunkSize_ShouldBeFourMiB()
    {
        VirtualDiskLayout.ChunkSizeBytes.Should().Be(4 * 1024 * 1024);
    }

    [Fact]
    public void Capacity_ShouldBeOneGiB()
    {
        VirtualDiskLayout.CapacityBytes.Should().Be(1024L * 1024 * 1024);
    }

    [Fact]
    public void MaxChunkPayload_ShouldCoverChunkSize()
    {
        VirtualDiskLayout.MaxChunkPayloadBytes.Should().BeGreaterThan(VirtualDiskLayout.ChunkSizeBytes);
    }
}
