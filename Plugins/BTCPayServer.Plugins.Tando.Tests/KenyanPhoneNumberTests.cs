using BTCPayServer.Plugins.Tando.Helper;

namespace BTCPayServer.Plugins.Tando.Tests;

[Trait("FastTest", "FastTest")]
public class KenyanPhoneNumberTests
{
    [Theory]
    [InlineData("0712345678", "254712345678")]
    [InlineData("+254712345678", "254712345678")]
    [InlineData("0112345678", "254112345678")]
    [InlineData("+254112345678", "254112345678")]
    [InlineData(" 0712345678 ", "254712345678")]
    public void Normalize_KenyanMobileFormats_ReturnsCanonicalNumber(string input, string expected)
        => Assert.Equal(expected, KenyanPhoneNumber.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-phone")]
    [InlineData("+255712345678")]
    [InlineData("+447123456789")]
    [InlineData("0212345678")]
    [InlineData("071234567")]
    [InlineData("07123456789")]
    [InlineData("0712 345678")]
    [InlineData("254712345678")]
    public void Normalize_UnsupportedOrMalformedNumber_ReturnsNull(string? input)
        => Assert.Null(KenyanPhoneNumber.Normalize(input));
}
