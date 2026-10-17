using Erp.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class WebApiClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnconfiguredClientUsesNavigationBaseUri(bool registerConfiguration)
    {
        using var services = CreateServices(registerConfiguration: registerConfiguration);
        using var scope = services.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<HttpClient>();

        Assert.Equal(new Uri("https://erp.example.test/company/"), client.BaseAddress);
        Assert.Equal(new Uri("https://erp.example.test/company/api/v1/inventory/balances"),
            new Uri(client.BaseAddress!, "api/v1/inventory/balances"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://[::1]:8080/company/")]
    [InlineData("https://localhost:8443/company/")]
    public void ConfiguredLoopbackAddressOverridesExternalNavigationBaseUri(string baseAddress)
    {
        using var services = CreateServices(baseAddress);
        using var scope = services.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<HttpClient>();

        Assert.Equal(new Uri(baseAddress), client.BaseAddress);
        Assert.Equal(new Uri(new Uri(baseAddress), "api/v1/reports/sales/summary"),
            new Uri(client.BaseAddress!, "api/v1/reports/sales/summary"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://127.0.0.1:8080/")]
    public void ClientForwardsTheExactIncomingCookieHeader(string? baseAddress)
    {
        const string cookie = ".AspNetCore.Cookies=test-session; erp-company=company-id";
        using var services = CreateServices(baseAddress, cookie);
        using var scope = services.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<HttpClient>();

        Assert.Equal(cookie, Assert.Single(client.DefaultRequestHeaders.GetValues("Cookie")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ClientOmitsMissingOrBlankCookieHeader(string? cookie)
    {
        using var services = CreateServices("http://127.0.0.1:8080/", cookie);
        using var scope = services.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<HttpClient>();

        Assert.False(client.DefaultRequestHeaders.Contains("Cookie"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("api/v1/")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("file:///C:/erp/")]
    [InlineData("https://erp.example.test/")]
    [InlineData("http://127.0.0.1.example.test/")]
    [InlineData("http://192.168.1.2/")]
    [InlineData("http://user@127.0.0.1/")]
    [InlineData("http://user:secret@localhost/")]
    [InlineData("http://localhost/?query=secret")]
    [InlineData("http://localhost/#fragment")]
    [InlineData("http://localhost/?")]
    [InlineData("http://localhost/#")]
    public void InvalidExplicitAddressFailsClearlyWithoutDisclosingItsValue(string baseAddress)
    {
        using var services = CreateServices(baseAddress, ".AspNetCore.Cookies=test-session");
        using var scope = services.CreateScope();

        var exception = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<HttpClient>());

        Assert.Equal("Web:ApiBaseAddress must be an absolute HTTP or HTTPS loopback URI without credentials, a query string, or a fragment.", exception.Message);
    }

    private static ServiceProvider CreateServices(string? baseAddress = null, string? cookie = null, bool registerConfiguration = true)
    {
        var services = new ServiceCollection();
        services.AddErpWeb();
        services.AddSingleton<NavigationManager, TestNavigation>();
        var context = new DefaultHttpContext();
        if (cookie is not null) context.Request.Headers.Cookie = cookie;
        services.AddSingleton<IHttpContextAccessor>(new FixedHttpContextAccessor { HttpContext = context });
        if (registerConfiguration)
        {
            var settings = baseAddress is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["Web:ApiBaseAddress"] = baseAddress };
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        }
        return services.BuildServiceProvider();
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://erp.example.test/company/", "https://erp.example.test/company/inventory/balances");

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            throw new InvalidOperationException("Resolving the API client must not navigate.");
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
