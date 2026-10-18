using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Erp.EndToEndTests;

public sealed partial class CriticalWorkflowTests(ITestOutputHelper output)
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
        try
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Save product" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(ProductDetailsUrl(), new() { Timeout = 60_000 });
        }
        catch (TimeoutException)
        {
            output.WriteLine($"ERP_E2E_PRODUCT_FAILURE_PATH={new Uri(page.Url).AbsolutePath}");
            var validation = await page.Locator("main [role='alert'], main .validation-message, main .validation-summary").AllTextContentsAsync();
            output.WriteLine($"ERP_E2E_PRODUCT_VALIDATION={string.Join(" | ", validation)}");
            output.WriteLine($"ERP_E2E_BROWSER_ERRORS={string.Join(" | ", browserErrors)}");
            throw;
        }
        Assert.DoesNotContain("Product validation failed", await page.Locator("body").InnerTextAsync(), StringComparison.OrdinalIgnoreCase);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Browser workflow product", Level = 1 })).ToBeVisibleAsync();
        var productAccessibility = await page.RunAxe();
        Assert.Empty(productAccessibility.Violations ?? []);
        await VerifyProductEditingAsync(page);
        await CrmWorkflow.VerifyAsync(page, baseUrl);

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
        var menu = page.Locator("button.menu-toggle[aria-controls='primary-sidebar']");
        await Assertions.Expect(menu).ToBeVisibleAsync();
        await Assertions.Expect(menu).ToHaveAccessibleNameAsync("Open navigation");
        await menu.ClickAsync();
        Assert.True(browserErrors.Count == 0, string.Join(Environment.NewLine, browserErrors));
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(menu).ToHaveAccessibleNameAsync("Close navigation");
        Assert.True(await page.GetByRole(AriaRole.Navigation, new() { Name = "Primary navigation" }).IsVisibleAsync());
        var sidebar = page.Locator("#primary-sidebar");
        await Assertions.Expect(sidebar).ToBeFocusedAsync();
        var firstNavigationControl = sidebar.Locator("a.brand");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(firstNavigationControl).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await Assertions.Expect(firstNavigationControl).Not.ToBeFocusedAsync();
        Assert.True(await sidebar.EvaluateAsync<bool>("element => element.contains(document.activeElement) && document.activeElement.checkVisibility()"), "Focus must wrap to a visible navigation control.");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(firstNavigationControl).ToBeFocusedAsync();
        var mobileAccessibility = await page.RunAxe();
        Assert.Empty(mobileAccessibility.Violations ?? []);
        var scrim = page.Locator("button.sidebar-scrim.is-visible");
        await Assertions.Expect(scrim).ToHaveAccessibleNameAsync("Close navigation");
        var scrimBounds = await scrim.BoundingBoxAsync();
        Assert.NotNull(scrimBounds);
        await scrim.ClickAsync(new() { Position = new() { X = scrimBounds.Width - 8, Y = 8 } });
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(menu).ToHaveAccessibleNameAsync("Open navigation");
        await Assertions.Expect(menu).ToBeFocusedAsync();
        await menu.ClickAsync();
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(menu).ToHaveAccessibleNameAsync("Close navigation");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(menu).ToHaveAccessibleNameAsync("Open navigation");
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

    private static async Task VerifyProductEditingAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        await page.GetByRole(AriaRole.Link, new() { Name = "Edit product", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Edit product", Level = 1, Exact = true })).ToBeVisibleAsync();
        var name = page.Locator("label").Filter(new() { HasText = "Name" }).Locator("input");
        await Assertions.Expect(name).ToHaveValueAsync("Browser workflow product");
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
        Assert.Empty((await page.RunAxe()).Violations ?? []);

        // The normalized value appears only after the server has saved and rebound the form.
        await name.FillAsync(" Browser workflow product updated ");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save product", Exact = true }).ClickAsync();
        await Assertions.Expect(name).ToHaveValueAsync("Browser workflow product updated");
        await page.GetByRole(AriaRole.Link, new() { Name = "View product", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Browser workflow product updated", Level = 1, Exact = true })).ToBeVisibleAsync();

        foreach (var action in new[] { (Button: "Archive", Status: "Archived"), (Button: "Reactivate", Status: "Active") })
        {
            // A force-loaded details page can render its links before the new circuit is ready.
            await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
            await page.GetByRole(AriaRole.Link, new() { Name = "Edit product", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Edit product", Level = 1, Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
            await page.GetByRole(AriaRole.Button, new() { Name = action.Button, Exact = true }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(ProductDetailsUrl(), new() { Timeout = 30_000 });
            await Assertions.Expect(page.Locator(".heading-badges .status-badge").Filter(new() { HasText = action.Status })).ToHaveTextAsync(action.Status);
            Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
        }
    }

    [GeneratedRegex("/products/[^/]+/details$")]
    private static partial Regex ProductDetailsUrl();
}
