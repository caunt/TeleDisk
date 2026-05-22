using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace TeleDisk.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddTeleDisk_ShouldThrow_WhenTokenMissing() => WithServices(services =>
    {
        var act = () => services.AddTeleDisk((string?)null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*TELEGRAM_BOT_TOKEN*");
    });

    [Fact]
    public void AddTeleDisk_ShouldRegisterCoreServices_WhenTokenExists() => WithProvider("123456:valid_token_value", provider =>
    {
        provider.GetRequiredService<global::TeleDisk.ExportRegistry>().Should().NotBeNull();
        provider.GetRequiredService<global::TeleDisk.Transport.Nbd.NbdEndpoint>().Should().NotBeNull();
    });

    [Fact]
    public void AddTeleDisk_ShouldRegisterBotTokenCatalog_WhenSingleTokenProvided() => WithProvider("123456:expected_token_value", provider =>
    {
        provider.GetRequiredService<global::TeleDisk.Infrastructure.Telegram.TelegramBotTokenCatalog>()
            .DefaultToken
            .Value
            .Should()
            .Be("123456:expected_token_value");
    });

    [Fact]
    public void AddTeleDisk_ShouldParseManyBotTokens() => WithProvider("123456:first_token,123456:second_token", provider =>
    {
        provider.GetRequiredService<global::TeleDisk.Infrastructure.Telegram.TelegramBotTokenCatalog>()
            .Tokens
            .Select(token => token.Value)
            .Should()
            .Equal("123456:first_token", "123456:second_token");
    });

    [Fact]
    public void AddTeleDisk_ShouldCreateScopedClientExportSessionPerScope() => WithProvider("123456:first_token", provider =>
    {
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstSession = firstScope.ServiceProvider.GetRequiredService<global::TeleDisk.Transport.Nbd.ClientExportSession>();
        var secondSession = secondScope.ServiceProvider.GetRequiredService<global::TeleDisk.Transport.Nbd.ClientExportSession>();
        firstSession.Should().NotBeSameAs(secondSession);
    });

    [Fact]
    public void AddTeleDisk_ShouldShareSingleExportSingletonAcrossClientScopes_WhenSingleTokenProvided() => WithProvider("123456:single_token", provider =>
    {
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstExport = firstScope.ServiceProvider.GetRequiredService<global::TeleDisk.Transport.Nbd.ClientExportSession>().Resolve("teledisk");
        var secondExport = secondScope.ServiceProvider.GetRequiredService<global::TeleDisk.Transport.Nbd.ClientExportSession>().Resolve("teledisk");
        firstExport.Should().BeSameAs(secondExport);
    });

    private static void WithProvider(string token, Action<ServiceProvider> assert)
    {
        WithServices(services =>
        {
            services.AddTeleDisk(token);
            using var provider = services.BuildServiceProvider();
            assert(provider);
        });
    }

    private static void WithServices(Action<ServiceCollection> assert)
    {
        var services = new ServiceCollection();
        assert(services);
    }
}
