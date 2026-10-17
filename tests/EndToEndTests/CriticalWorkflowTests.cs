using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Erp.EndToEndTests;

public sealed class CriticalWorkflowTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AuthenticatedNavigationAndInventoryWorkAcrossDesktopAndMobile()
    {
        var baseUrl = Environment.GetEnvironmentVariable("ERP_E2E_BASE_URL");
        var email = Environment.GetEnvironmentVariable("ERP_E2E_EMAIL");
        var password = Environment.GetEnvironmentVariable("ERP_E2E_PASSWORD");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        output.WriteLine("ERP_E2E_PHASE=playwright-create");
        using var playwright = await Playwright.CreateAsync();
        output.WriteLine("ERP_E2E_PHASE=chromium-launch");
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var browserErrors = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") browserErrors.Add(message.Text);
        };
        output.WriteLine("ERP_E2E_PHASE=login-navigation");
        await page.GotoAsync(new Uri(new Uri(baseUrl), "/account/login").ToString());
        Assert.Equal(0, await page.Locator("input:not([type='hidden'])").EvaluateAllAsync<int>("elements => elements.filter(element => !element.labels?.length && !element.getAttribute('aria-label') && !element.getAttribute('aria-labelledby')).length"));
        output.WriteLine("ERP_E2E_PHASE=antiforgery-token");
        var antiforgeryToken = await page.Locator("input[name='__RequestVerificationToken']").InputValueAsync();
        var loginForm = page.Context.APIRequest.CreateFormData();
        loginForm.Set("email", email);
        loginForm.Set("password", password);
        loginForm.Set("__RequestVerificationToken", antiforgeryToken);
        output.WriteLine("ERP_E2E_PHASE=login-submit");
        var loginResponse = await page.Context.APIRequest.PostAsync(new Uri(new Uri(baseUrl), "/account/login").ToString(), new()
        {
            Form = loginForm
        });
        output.WriteLine($"ERP_E2E_LOGIN_STATUS={loginResponse.Status}");
        Assert.True(loginResponse.Ok, $"Authentication setup returned HTTP {loginResponse.Status}.");
        output.WriteLine("ERP_E2E_PHASE=workflow");
        await page.GotoAsync(baseUrl);
        Assert.DoesNotContain("/account/login", page.Url, StringComparison.Ordinal);
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");

        var body = await page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("Organization Organization", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Something went wrong", body, StringComparison.OrdinalIgnoreCase);

        await page.GotoAsync(new Uri(new Uri(baseUrl), "/products/new").ToString());
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        Assert.DoesNotContain("_error", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        var productSku = $"E2E-{Guid.NewGuid():N}"[..16].ToUpperInvariant();
        await page.Locator("label").Filter(new() { HasText = "SKU" }).Locator("input").FillAsync(productSku);
        await page.Locator("label").Filter(new() { HasText = "Name" }).Locator("input").FillAsync("Browser workflow product");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save product" }).ClickAsync();
        await page.WaitForURLAsync("**/products/*/details", new() { Timeout = 60_000 });
        Assert.DoesNotContain("Product validation failed", await page.Locator("body").InnerTextAsync(), StringComparison.OrdinalIgnoreCase);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Browser workflow product", Level = 1 })).ToBeVisibleAsync();
        var productAccessibility = await page.RunAxe();
        Assert.Empty(productAccessibility.Violations ?? []);

        await page.GotoAsync(new Uri(new Uri(baseUrl), "/products").ToString());
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Products and services", Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText(productSku, new() { Exact = true })).ToBeVisibleAsync();
        Assert.DoesNotContain("Product validation failed", await page.Locator("body").InnerTextAsync(), StringComparison.OrdinalIgnoreCase);

        await page.GotoAsync(new Uri(new Uri(baseUrl), "/inventory/balances").ToString());
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        Assert.Equal(1, await page.Locator("h1").CountAsync());
        Assert.True(await page.GetByRole(AriaRole.Heading, new() { Level = 1 }).IsVisibleAsync());
        Assert.False(await page.Locator(".error-panel").IsVisibleAsync());
        Assert.Equal(0, await page.Locator("[id]").EvaluateAllAsync<int>("elements => elements.length - new Set(elements.map(element => element.id)).size"));
        await page.WaitForFunctionAsync("() => document.title.trim().length > 0");
        var desktopAccessibility = await page.RunAxe();
        Assert.Empty(desktopAccessibility.Violations ?? []);

        var criticalRoutes = new[]
        {
            "/contacts/new",
            "/crm/leads/new",
            "/crm/opportunities/new",
            "/sales/quotations/new",
            "/sales/orders",
            "/purchasing/orders/new",
            "/purchasing/orders",
            "/inventory/moves",
            "/inventory/moves/new",
            "/inventory/reservations",
            "/accounting/invoices",
            "/accounting/entries",
            "/reports/financial",
            "/organization",
            "/administration/users",
            "/administration/roles",
            "/administration/modules",
            "/administration/queues",
            "/administration/data-maintenance",
            "/help",
            "/about"
        };
        foreach (var route in criticalRoutes)
        {
            await page.GotoAsync(new Uri(new Uri(baseUrl), route).ToString());
            await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
            Assert.Equal(1, await page.Locator("h1").CountAsync());
            Assert.False(await page.Locator(".error-panel").IsVisibleAsync());
            Assert.DoesNotContain("Something went wrong", await page.Locator("body").InnerTextAsync(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("_error", await page.Locator("body").InnerTextAsync(), StringComparison.Ordinal);
        }
        await MultiCompanyWorkflow.VerifyAsync(page, baseUrl, email, password, productSku);
        Assert.True(browserErrors.Count == 0, string.Join(Environment.NewLine, browserErrors));

        await page.GotoAsync(new Uri(new Uri(baseUrl), "/inventory/balances").ToString());
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");

        await page.SetViewportSizeAsync(390, 844);
        var menu = page.GetByRole(AriaRole.Button, new() { Name = "Menu" });
        Assert.True(await menu.IsVisibleAsync());
        await menu.ClickAsync();
        Assert.True(browserErrors.Count == 0, string.Join(Environment.NewLine, browserErrors));
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "true");
        Assert.True(await page.GetByRole(AriaRole.Navigation, new() { Name = "Primary navigation" }).IsVisibleAsync());
        var closeNavigation = page.Locator("#primary-sidebar").GetByRole(AriaRole.Button, new() { Name = "Close navigation", Exact = true });
        await Assertions.Expect(closeNavigation).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        Assert.True(await page.EvaluateAsync<bool>("() => document.querySelector('#primary-sidebar').contains(document.activeElement)"));
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(closeNavigation).ToBeFocusedAsync();
        var mobileAccessibility = await page.RunAxe();
        Assert.Empty(mobileAccessibility.Violations ?? []);
        await page.Locator("#primary-sidebar").GetByRole(AriaRole.Button, new() { Name = "Close navigation", Exact = true }).ClickAsync();
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(menu).ToBeFocusedAsync();
        await menu.ClickAsync();
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "true");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(menu).ToBeFocusedAsync();
        foreach (var route in new[] { "/organization", "/administration/users", "/administration/roles", "/administration/data-maintenance" })
        {
            await page.GotoAsync(new Uri(new Uri(baseUrl), route).ToString());
            await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
            Assert.Equal(1, await page.Locator("h1").CountAsync());
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"Horizontal page overflow at mobile width on {route}.");
            Assert.Empty((await page.RunAxe()).Violations ?? []);
        }
        Assert.True(browserErrors.Count == 0, string.Join(Environment.NewLine, browserErrors));
        await page.GotoAsync(new Uri(new Uri(baseUrl), "/administration/users").ToString());
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "user-administration-mobile.png") });
        await page.SetViewportSizeAsync(1440, 1000);
        await page.GotoAsync(new Uri(new Uri(baseUrl), "/organization").ToString());
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "company-administration-desktop.png") });
    }
}
