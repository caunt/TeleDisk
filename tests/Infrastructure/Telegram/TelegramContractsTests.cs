using System.Text.Json;
using FluentAssertions;
using TeleDisk.Infrastructure.Telegram;

namespace TeleDisk.Tests.Infrastructure.Telegram;

public sealed class TelegramContractsTests
{
    [Fact]
    public void TelegramBotToken_ShouldStoreValue()
    {
        new TelegramBotToken("abc").Value.Should().Be("abc");
    }

    [Fact]
    public void TelegramBotDescription_ShouldDeserializeDescriptionProperty()
    {
        var model = JsonSerializer.Deserialize<TelegramBotDescription>("""{"description":"disk"}""");

        model.Should().NotBeNull();
        model?.Description.Should().Be("disk");
    }

    [Fact]
    public void TelegramApiResponse_ShouldDeserializeOptionalFields()
    {
        var model = JsonSerializer.Deserialize<TelegramApiResponse<string>>("""{"ok":true,"result":"file-id","description":null}""");

        model.Should().NotBeNull();
        model?.Ok.Should().BeTrue();
        model?.Result.Should().Be("file-id");
        model?.Description.Should().BeNull();
    }
}
