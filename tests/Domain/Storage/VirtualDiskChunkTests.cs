using FluentAssertions;
using TeleDisk.Domain.Storage;

namespace TeleDisk.Tests.Domain.Storage;

public sealed class VirtualDiskChunkTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void IsZero_ShouldBeTrue_WhenFileIdMissing(string? fileId)
    {
        var chunk = new VirtualDiskChunk(fileId, "hash");

        chunk.IsZero.Should().BeTrue();
    }

    [Fact]
    public void IsZero_ShouldBeFalse_WhenFileIdPresent()
    {
        var chunk = new VirtualDiskChunk("file-id", "hash");

        chunk.IsZero.Should().BeFalse();
    }

    [Fact]
    public void Zero_ShouldExposeEmptyMetadata()
    {
        VirtualDiskChunk.Zero.FileId.Should().BeNull();
        VirtualDiskChunk.Zero.Sha256.Should().BeNull();
        VirtualDiskChunk.Zero.IsZero.Should().BeTrue();
    }
}
