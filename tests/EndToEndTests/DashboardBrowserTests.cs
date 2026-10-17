using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Erp.EndToEndTests;

public sealed class DashboardBrowserTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReportDashboardsAreAccessibleResponsiveAndUseLocalAssets()
    {
        var baseUrl = Environment.GetEnvironmentVariable("ERP_E2E_BASE_URL");
        var email = Environment.GetEnvironmentVariable("ERP_E2E_EMAIL");
        var password = Environment.GetEnvironmentVariable("ERP_E2E_PASSWORD");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        var applicationUri = new Uri(baseUrl);
        output.WriteLine("ERP_E2E_PHASE=playwright-create");
        using var playwright = await Playwright.CreateAsync();
        output.WriteLine("ERP_E2E_PHASE=chromium-launch");
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var browserErrors = new List<string>();
        var externalAssets = new HashSet<string>(StringComparer.Ordinal);
        page.PageError += (_, error) => browserErrors.Add(error);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") browserErrors.Add(message.Text);
        };
        page.Request += (_, request) =>
        {
            if (request.ResourceType is "script" or "stylesheet" or "font" or "image"
                && Uri.TryCreate(request.Url, UriKind.Absolute, out var assetUri)
                && assetUri.Scheme is "http" or "https"
                && assetUri.GetLeftPart(UriPartial.Authority) != applicationUri.GetLeftPart(UriPartial.Authority))
            {
                externalAssets.Add(request.Url);
            }
        };

        output.WriteLine("ERP_E2E_PHASE=login-navigation");
        await page.GotoAsync(new Uri(applicationUri, "/account/login").ToString());
        var loginForm = page.Context.APIRequest.CreateFormData();
        loginForm.Set("email", email);
        loginForm.Set("password", password);
        output.WriteLine("ERP_E2E_PHASE=antiforgery-token");
        loginForm.Set("__RequestVerificationToken", await page.Locator("input[name='__RequestVerificationToken']").InputValueAsync());
        output.WriteLine("ERP_E2E_PHASE=login-submit");
        var login = await page.Context.APIRequest.PostAsync(new Uri(applicationUri, "/account/login").ToString(), new() { Form = loginForm });
        output.WriteLine($"ERP_E2E_LOGIN_STATUS={login.Status}");
        Assert.True(login.Ok, $"Authentication setup returned HTTP {login.Status}.");

        output.WriteLine("ERP_E2E_PHASE=workflow");
        await VisitAsync(page, applicationUri, "/");
        await AssertLocalBootstrapAsync(page, applicationUri);
        await VisitAsync(page, applicationUri, "/reports/dashboard");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Company performance", Level = 1, Exact = true })).ToBeVisibleAsync();
        await AssertDashboardChartsAsync(page);
        await AssertAccessibleAsync(page, "Report dashboard at desktop width");
        await VerifyDateFilterAsync(page);
        await VerifyCurrencySelectorAsync(page);
        await AssertDashboardChartsAsync(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "reports-dashboard-desktop.png"), FullPage = true });

        var reports = new[]
        {
            (Route: "/reports/sales", Heading: "Sales analysis"),
            (Route: "/reports/purchasing", Heading: "Purchase summary"),
            (Route: "/reports/inventory", Heading: "Inventory reconciliation"),
            (Route: "/reports/financial", Heading: "Financial statements"),
            (Route: "/reports/receivables", Heading: "Accounts receivable aging"),
            (Route: "/reports/payables", Heading: "Accounts payable aging"),
            (Route: "/reports/cash", Heading: "Cash and payment summary")
        };
        foreach (var width in new[] { 1440, 390 })
        {
            await page.SetViewportSizeAsync(width, 900);
            foreach (var report in reports)
            {
                await VisitAsync(page, applicationUri, report.Route);
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = report.Heading, Level = 1, Exact = true })).ToBeVisibleAsync();
                await WaitForReportAsync(page);
                await AssertHealthyPageAsync(page, report.Route);
                await AssertAccessibleAsync(page, $"{report.Heading} at width {width}");
            }
        }

        await VisitAsync(page, applicationUri, "/reports/dashboard");
        await AssertDashboardChartsAsync(page);
        await AssertAccessibleAsync(page, "Report dashboard at mobile width");
        await VerifyMobileNavigationAsync(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "reports-dashboard-mobile.png"), FullPage = true });

        Assert.True(browserErrors.Count == 0, string.Join(Environment.NewLine, browserErrors));
        Assert.True(externalAssets.Count == 0, $"Runtime assets must be self-hosted:{Environment.NewLine}{string.Join(Environment.NewLine, externalAssets)}");
    }

    private static async Task VisitAsync(IPage page, Uri applicationUri, string route)
    {
        var response = await page.GotoAsync(new Uri(applicationUri, route).ToString());
        Assert.NotNull(response);
        Assert.True(response.Ok, $"Navigation to {route} returned HTTP {response.Status}.");
        Assert.DoesNotContain("/account/login", page.Url, StringComparison.Ordinal);
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeVisibleAsync();
        await AssertHealthyPageAsync(page, route);
    }

    private static async Task AssertHealthyPageAsync(IPage page, string route)
    {
        Assert.Equal(1, await page.Locator("main h1").CountAsync());
        Assert.Equal(0, await page.Locator(".error-panel").CountAsync());
        Assert.Equal(0, await page.Locator("main .notice-error").CountAsync());
        var content = await page.Locator("main").InnerTextAsync();
        Assert.DoesNotContain("_error", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Something went wrong", content, StringComparison.OrdinalIgnoreCase);
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"Horizontal page overflow on {route}.");
        Assert.Equal(0, await page.Locator("[id]").EvaluateAllAsync<int>("elements => elements.length - new Set(elements.map(element => element.id)).size"));
    }

    private static async Task WaitForReportAsync(IPage page)
    {
        await Assertions.Expect(page.Locator("main [role='status']").Filter(new() { HasText = "Loading" })).ToHaveCountAsync(0);
    }

    private static async Task AssertLocalBootstrapAsync(IPage page, Uri applicationUri)
    {
        var bootstrap = page.Locator("link[rel='stylesheet'][href*='bootstrap']");
        Assert.Equal(1, await bootstrap.CountAsync());
        var href = await bootstrap.GetAttributeAsync("href");
        Assert.False(string.IsNullOrWhiteSpace(href));
        var assetUri = new Uri(applicationUri, href);
        Assert.Equal(applicationUri.GetLeftPart(UriPartial.Authority), assetUri.GetLeftPart(UriPartial.Authority));
        var response = await page.Context.APIRequest.GetAsync(assetUri.ToString());
        Assert.Equal(200, response.Status);
        Assert.Contains("Bootstrap", await response.TextAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.True(await bootstrap.EvaluateAsync<bool>("element => element.sheet !== null"), "The self-hosted Bootstrap stylesheet did not load.");
        var assetUrls = await page.Locator("link[rel='stylesheet'][href], script[src]").EvaluateAllAsync<string[]>("elements => elements.map(element => element.href || element.src)");
        Assert.All(assetUrls, url => Assert.Equal(applicationUri.GetLeftPart(UriPartial.Authority), new Uri(url).GetLeftPart(UriPartial.Authority)));
    }

    private static async Task AssertDashboardChartsAsync(IPage page)
    {
        await WaitForReportAsync(page);
        await AssertHealthyPageAsync(page, "/reports/dashboard");
        var charts = page.Locator("main figure.report-chart");
        Assert.True(await charts.CountAsync() > 0, "The dashboard did not render any chart panels.");
        foreach (var chart in await charts.AllAsync())
        {
            var svg = chart.Locator("svg[role='img']");
            if (await svg.CountAsync() == 0)
            {
                await Assertions.Expect(chart.Locator(".chart-empty")).ToBeVisibleAsync();
                Assert.Contains("No ", await chart.InnerTextAsync(), StringComparison.OrdinalIgnoreCase);
                continue;
            }

            Assert.Equal(1, await svg.CountAsync());
            await Assertions.Expect(svg).ToBeVisibleAsync();
            Assert.True(await svg.EvaluateAsync<bool>("element => (element.getAttribute('aria-labelledby') || '').split(/\\s+/).filter(Boolean).length > 0 && element.getAttribute('aria-labelledby').split(/\\s+/).filter(Boolean).every(id => document.getElementById(id)?.textContent.trim().length > 0)"), "Chart images need nonempty accessible names and descriptions.");
            Assert.DoesNotContain("NaN", await svg.InnerHTMLAsync(), StringComparison.Ordinal);
            Assert.DoesNotContain("Infinity", await svg.InnerHTMLAsync(), StringComparison.Ordinal);
            var chartData = chart.Locator("details.chart-data");
            Assert.Equal(1, await chartData.CountAsync());
            await chartData.Locator("summary").ClickAsync();
            await Assertions.Expect(chartData.Locator("table")).ToBeVisibleAsync();
            Assert.True(await chartData.Locator("table tbody tr").CountAsync() > 0, "Nonempty charts need a tabular alternative.");
            await chartData.Locator("summary").ClickAsync();
        }
    }

    private static async Task VerifyDateFilterAsync(IPage page)
    {
        var from = page.GetByLabel("From", new() { Exact = true });
        var to = page.GetByLabel("To", new() { Exact = true });
        var originalFrom = await from.InputValueAsync();
        var originalTo = await to.InputValueAsync();
        await from.FillAsync(DateOnly.Parse(originalTo, System.Globalization.CultureInfo.InvariantCulture).AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("From date must be on or before To date.");
        Assert.Equal(0, await page.Locator(".error-panel").CountAsync());
        await from.FillAsync(originalFrom);
        await to.FillAsync(originalTo);
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply filters", Exact = true }).ClickAsync();
        await WaitForReportAsync(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
    }

    private static async Task VerifyCurrencySelectorAsync(IPage page)
    {
        var currency = page.GetByLabel("Chart currency", new() { Exact = true });
        Assert.Equal(1, await currency.CountAsync());
        var options = await currency.Locator("option").EvaluateAllAsync<string[]>("elements => elements.filter(element => !element.disabled && element.value).map(element => element.value)");
        Assert.NotEmpty(options);
        var original = await currency.InputValueAsync();
        foreach (var value in options.Take(2))
        {
            await currency.SelectOptionAsync(value);
            await Assertions.Expect(currency).ToHaveValueAsync(value);
            await AssertDashboardChartsAsync(page);
        }
        await currency.SelectOptionAsync(original);
    }

    private static async Task VerifyMobileNavigationAsync(IPage page)
    {
        var menu = page.Locator("button.menu-toggle[aria-controls='primary-sidebar']");
        await Assertions.Expect(menu).ToBeVisibleAsync();
        await Assertions.Expect(menu).ToHaveAccessibleNameAsync("Open navigation");
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "false");
        await menu.ClickAsync();
        await Assertions.Expect(menu).ToHaveAccessibleNameAsync("Close navigation");
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "true");
        var sidebar = page.Locator("#primary-sidebar");
        await Assertions.Expect(sidebar).ToBeFocusedAsync();
        var firstControl = sidebar.Locator("a.brand");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(firstControl).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await Assertions.Expect(firstControl).Not.ToBeFocusedAsync();
        Assert.True(await sidebar.EvaluateAsync<bool>("element => element.contains(document.activeElement) && document.activeElement.checkVisibility()"), "Focus must wrap to a visible navigation control, not a link inside a collapsed group.");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(firstControl).ToBeFocusedAsync();
        await AssertAccessibleAsync(page, "Mobile report dashboard with open navigation");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(menu).ToHaveAccessibleNameAsync("Open navigation");
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(menu).ToBeFocusedAsync();
    }

    private static async Task AssertAccessibleAsync(IPage page, string context)
    {
        var violations = (await page.RunAxe()).Violations ?? [];
        Assert.True(violations.Length == 0, $"Accessibility violations on {context}:{Environment.NewLine}{string.Join(Environment.NewLine, violations.Select(violation => $"{violation.Id}: {violation.Description}; affected elements: {string.Join(", ", violation.Nodes.Select(node => System.Text.Json.JsonSerializer.Serialize(node.Target)))}"))}");
    }
}
