using Microsoft.Playwright;

namespace Erp.EndToEndTests;

public sealed class FormErrorBrowserTests
{
    [Fact]
    public async Task CreateFormsDoNotDisplayLiteralErrorText()
    {
        var baseUrl = Environment.GetEnvironmentVariable("ERP_E2E_BASE_URL");
        var email = Environment.GetEnvironmentVariable("ERP_E2E_EMAIL");
        var password = Environment.GetEnvironmentVariable("ERP_E2E_PASSWORD");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        var browserErrors = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);

        await page.GotoAsync(new Uri(new Uri(baseUrl), "/account/login").ToString());
        var loginForm = page.Context.APIRequest.CreateFormData();
        loginForm.Set("email", email);
        loginForm.Set("password", password);
        loginForm.Set("__RequestVerificationToken", await page.Locator("input[name='__RequestVerificationToken']").InputValueAsync());
        var login = await page.Context.APIRequest.PostAsync(new Uri(new Uri(baseUrl), "/account/login").ToString(), new() { Form = loginForm });
        Assert.True(login.Ok, $"Authentication setup returned HTTP {login.Status}.");

        var forms = new[]
        {
            (Route: "/crm/leads/new", Heading: "New lead"),
            (Route: "/sales/quotations/new", Heading: "New quotation"),
            (Route: "/purchasing/orders/new", Heading: "New purchase order"),
            (Route: "/contacts/new", Heading: "New contact"),
            (Route: "/products/new", Heading: "New product or service")
        };
        foreach (var width in new[] { 1440, 390 })
        {
            await page.SetViewportSizeAsync(width, 900);
            foreach (var form in forms)
            {
                await page.GotoAsync(new Uri(new Uri(baseUrl), form.Route).ToString());
                await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = form.Heading, Level = 1, Exact = true })).ToBeVisibleAsync();
                Assert.True(await page.Locator("main form").CountAsync() > 0, $"The form did not load on {form.Route}.");
                Assert.DoesNotContain("_error", await page.Locator("main").InnerTextAsync(), StringComparison.Ordinal);
                Assert.Equal(0, await page.Locator("main [role='alert']").CountAsync());
                Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"Horizontal page overflow on {form.Route} at width {width}.");
            }
        }

        Assert.Empty(browserErrors);
    }
}
