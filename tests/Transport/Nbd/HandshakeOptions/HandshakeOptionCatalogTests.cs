using System.Reflection;
using TeleDisk.Transport.Nbd;

namespace TeleDisk.Tests.NbdProtocol.HandshakeOptions;

public sealed class HandshakeOptionCatalogTests
{
    private static readonly Type EndpointType = typeof(NbdEndpoint);

    [Theory]
    [InlineData("NbdOptionExportName", 1u)]
    [InlineData("NbdOptionAbort", 2u)]
    [InlineData("NbdOptionList", 3u)]
    [InlineData("NbdOptionPeekExport", 4u)]
    [InlineData("NbdOptionStartTls", 5u)]
    [InlineData("NbdOptionInfo", 6u)]
    [InlineData("NbdOptionGo", 7u)]
    [InlineData("NbdOptionStructuredReply", 8u)]
    [InlineData("NbdOptionListMetaContext", 9u)]
    [InlineData("NbdOptionSetMetaContext", 10u)]
    [InlineData("NbdOptionExtendedHeaders", 11u)]
    public void UsesExpectedNegotiationOptionCode(string constantName, uint expectedCode)
    {
        var field = EndpointType.GetField(constantName, BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull();
        field!.GetRawConstantValue().Should().Be(expectedCode);
    }

    [Fact]
    public void UsesUniqueNegotiationOptionCodes()
    {
        var optionCodes = EndpointType
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(uint) && field.Name.StartsWith("NbdOption", StringComparison.Ordinal))
            .Select(static field => (uint)(field.GetRawConstantValue() ?? 0u))
            .OrderBy(static code => code)
            .ToArray();

        optionCodes.Should().Equal([1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 9u, 10u, 11u]);
    }
}
