using FluentAssertions;
using TeleDisk.Domain.Storage;

namespace TeleDisk.Tests.Domain.Storage;

public sealed class VirtualDiskIndexTests
{
    [Fact]
    public void CreateDefault_ShouldUseLayoutValuesAndEmptyChunks()
    {
        var index = VirtualDiskIndex.CreateDefault();

        index.CapacityBytes.Should().Be(VirtualDiskLayout.CapacityBytes);
        index.ChunkSizeBytes.Should().Be(VirtualDiskLayout.ChunkSizeBytes);
        index.Chunks.Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_ShouldReturnDefault_WhenPersistedIndexIsNull()
    {
        var index = VirtualDiskIndex.Sanitize(null);

        index.Should().BeEquivalentTo(VirtualDiskIndex.CreateDefault());
    }

    [Fact]
    public void Sanitize_ShouldKeepOnlyValidChunks()
    {
        var maxChunkIndex = (VirtualDiskLayout.CapacityBytes - 1) / VirtualDiskLayout.ChunkSizeBytes;
        var persisted = new VirtualDiskIndex(
            1,
            1,
            new Dictionary<long, VirtualDiskChunk>
            {
                [-1] = new("invalid-negative", "x"),
                [maxChunkIndex + 1] = new("invalid-overflow", "x"),
                [1] = new("", "x"),
                [2] = new("valid", "x")
            });

        var index = VirtualDiskIndex.Sanitize(persisted);

        index.Chunks.Keys.Should().ContainSingle().Which.Should().Be(2);
        index.Chunks[2].FileId.Should().Be("valid");
        index.CapacityBytes.Should().Be(VirtualDiskLayout.CapacityBytes);
        index.ChunkSizeBytes.Should().Be(VirtualDiskLayout.ChunkSizeBytes);
    }
}
