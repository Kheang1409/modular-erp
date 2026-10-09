using Erp.Host.Hosting;
using Microsoft.AspNetCore.Http;

namespace Erp.UnitTests;

public sealed class SecurityHeaderPolicyTests
{
    [Fact]
    public void AppliesBrowserSecurityHeaders()
    {
        var headers = new HeaderDictionary();

        SecurityHeaderPolicy.Apply(headers);

        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        Assert.Contains("frame-ancestors 'none'", headers["Content-Security-Policy"].ToString(), StringComparison.Ordinal);
        Assert.Contains("geolocation=()", headers["Permissions-Policy"].ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("request-01", true)]
    [InlineData("trace_ABC.123", true)]
    [InlineData("", false)]
    [InlineData("contains space", false)]
    [InlineData("bad\r\nheader", false)]
    public void CorrelationIdentifiersAreStrictlyValidated(string value, bool expected) =>
        Assert.Equal(expected, SecurityHeaderPolicy.IsSafeCorrelationId(value));
}
