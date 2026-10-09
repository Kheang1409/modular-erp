using System.Net;
using System.Net.Http.Json;
using Erp.Host;
using Erp.Host.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erp.UnitTests;

public sealed class AccountFormBindingTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("/dashboard?source=login")]
    public async Task LoginFormBindsWithOptionalReturnUrlAsync(string? returnUrl)
    {
        await using var app = CreateApplication();
        await app.StartAsync();
        using var client = CreateClient(app);
        var fields = new Dictionary<string, string>
        {
            ["Email"] = "form-binding@example.test",
            ["Password"] = "form-binding-password"
        };
        if (returnUrl is not null) fields["ReturnUrl"] = returnUrl;
        using var content = new FormUrlEncodedContent(fields);

        using var response = await client.PostAsync("/login", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var form = await response.Content.ReadFromJsonAsync<LoginForm>();
        Assert.NotNull(form);
        Assert.Equal(fields["Email"], form.Email);
        Assert.Equal(fields["Password"], form.Password);
        Assert.Equal(returnUrl, form.ReturnUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/dashboard?source=company")]
    public async Task SwitchCompanyFormBindsWithOptionalReturnUrlAsync(string? returnUrl)
    {
        await using var app = CreateApplication();
        await app.StartAsync();
        using var client = CreateClient(app);
        var companyId = Guid.NewGuid();
        var fields = new Dictionary<string, string> { ["CompanyId"] = companyId.ToString() };
        if (returnUrl is not null) fields["ReturnUrl"] = returnUrl;
        using var content = new FormUrlEncodedContent(fields);

        using var response = await client.PostAsync("/company/switch", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var form = await response.Content.ReadFromJsonAsync<SwitchCompanyForm>();
        Assert.NotNull(form);
        Assert.Equal(companyId, form.CompanyId);
        Assert.Equal(returnUrl, form.ReturnUrl);
    }

    private static WebApplication CreateApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapPost("/login", ([FromForm] LoginForm form) => Results.Json(form)).DisableAntiforgery();
        app.MapPost("/company/switch", ([FromForm] SwitchCompanyForm form) => Results.Json(form)).DisableAntiforgery();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        Assert.NotNull(addresses);
        return new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri(Assert.Single(addresses.Addresses), UriKind.Absolute)
        };
    }
}
