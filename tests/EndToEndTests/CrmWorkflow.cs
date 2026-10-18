using System.Text.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Erp.EndToEndTests;

internal static partial class CrmWorkflow
{
    public static async Task VerifyAsync(IPage page, string baseUrl)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stages = await GetJsonAsync(page, baseUrl, "/api/v1/crm/stages/?includeInactive=true");
        var sequence = stages.EnumerateArray().Select(stage => stage.GetProperty("sequence").GetInt32()).DefaultIfEmpty(0).Max() + 10;
        var openName = $"Browser open {suffix}";
        var wonName = $"Browser won {suffix}";
        await VisitAsync(page, baseUrl, "/crm/stages", "Pipeline stages");
        await AddStageAsync(page, openName, sequence, false);
        await AddStageAsync(page, wonName, sequence + 10, true);
        stages = await GetJsonAsync(page, baseUrl, "/api/v1/crm/stages/?includeInactive=true");
        var openId = stages.EnumerateArray().Single(stage => stage.GetProperty("name").GetString() == openName).GetProperty("id").GetGuid();
        var wonId = stages.EnumerateArray().Single(stage => stage.GetProperty("name").GetString() == wonName).GetProperty("id").GetGuid();

        var title = $"Browser lead {suffix}";
        await VisitAsync(page, baseUrl, "/crm/leads/new", "New lead");
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync(title);
        await page.GetByLabel("Expected revenue", new() { Exact = true }).FillAsync("1200");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save lead", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(LeadDetailsUrl(), new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1, Exact = true })).ToBeVisibleAsync();
        var leadId = Guid.Parse(new Uri(page.Url).Segments[^2].Trim('/'));
        var leadPath = $"/crm/leads/{leadId}/details";
        await ExpectPathAsync(page, leadPath);
        await FollowAsync(page, "Edit");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Edit lead", Level = 1, Exact = true })).ToBeVisibleAsync();
        title = $"Updated browser lead {suffix}";
        var leadTitle = page.GetByLabel("Title", new() { Exact = true });
        await leadTitle.FillAsync($" {title} ");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save lead", Exact = true }).ClickAsync();
        await Assertions.Expect(leadTitle).ToHaveValueAsync(title);
        await VisitAsync(page, baseUrl, leadPath, title);
        await AssignAsync(page, leadPath);
        await VerifyActivityAsync(page, leadPath, $"Browser follow-up {suffix}");
        await VerifyArchiveAsync(page, leadPath);

        var lead = await GetJsonAsync(page, baseUrl, $"/api/v1/crm/leads/{leadId}");
        var qualified = await PostJsonAsync(page, baseUrl, $"/api/v1/crm/leads/{leadId}/qualify",
            new { concurrencyStamp = lead.GetProperty("concurrencyStamp").GetString() });
        var opportunityTitle = $"Browser opportunity {suffix}";
        var opportunity = await PostJsonAsync(page, baseUrl, $"/api/v1/crm/leads/{leadId}/convert",
            new { concurrencyStamp = qualified.GetProperty("concurrencyStamp").GetString(), stageId = openId, title = opportunityTitle });
        Assert.Equal(leadId, opportunity.GetProperty("sourceLeadId").GetGuid());
        var opportunityId = opportunity.GetProperty("id").GetGuid();
        var opportunityPath = $"/crm/opportunities/{opportunityId}";
        await VisitAsync(page, baseUrl, leadPath, title);
        await Assertions.Expect(page.Locator("main .eyebrow")).ToHaveTextAsync("Converted");
        await VisitAsync(page, baseUrl, opportunityPath, opportunityTitle);
        await AssignAsync(page, opportunityPath);

        await FollowAsync(page, "Manage");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Manage opportunity", Level = 1, Exact = true })).ToBeVisibleAsync();
        opportunityTitle = $"Updated browser opportunity {suffix}";
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync(opportunityTitle);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await ExpectPathAsync(page, opportunityPath);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = opportunityTitle, Level = 1, Exact = true })).ToBeVisibleAsync();
        await FollowAsync(page, "Manage");
        var move = page.Locator("form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Move", Exact = true }) });
        await move.GetByRole(AriaRole.Combobox, new() { Name = "Stage", Exact = true }).SelectOptionAsync(wonId.ToString());
        await move.GetByRole(AriaRole.Button, new() { Name = "Move", Exact = true }).ClickAsync();
        await ExpectPathAsync(page, opportunityPath);
        await Assertions.Expect(page.Locator("main .eyebrow")).ToContainTextAsync($"Won · {wonName}");
        await VerifyArchiveAsync(page, opportunityPath);

        foreach (var width in new[] { 1440, 390 })
        {
            await page.SetViewportSizeAsync(width, 900);
            await VisitAsync(page, baseUrl, opportunityPath, opportunityTitle);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "CRM detail page overflows the viewport.");
            Assert.Empty((await page.RunAxe()).Violations ?? []);
        }
        await page.SetViewportSizeAsync(1440, 1000);
    }

    private static async Task AddStageAsync(IPage page, string name, int sequence, bool won)
    {
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync(name);
        await page.GetByLabel("Sequence", new() { Exact = true }).FillAsync(sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.GetByLabel("Default probability", new() { Exact = true }).FillAsync(won ? "100" : "25");
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "Won stage", Exact = true }).SetCheckedAsync(won);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add stage", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Cell, new() { Name = name, Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
    }

    private static async Task AssignAsync(IPage page, string detailsPath)
    {
        await FollowAsync(page, "Assign");
        var users = page.GetByRole(AriaRole.Combobox, new() { Name = "Company user", Exact = true });
        await Assertions.Expect(users).ToBeVisibleAsync();
        var values = await users.Locator("option").EvaluateAllAsync<string[]>("elements => elements.filter(element => !element.disabled && element.value).map(element => element.value)");
        Assert.NotEmpty(values);
        await users.SelectOptionAsync(values[0]);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save assignment", Exact = true }).ClickAsync();
        await ExpectPathAsync(page, detailsPath);
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
    }

    private static async Task VerifyActivityAsync(IPage page, string detailsPath, string subject)
    {
        await FollowAsync(page, "Add activity");
        await page.GetByLabel("Subject", new() { Exact = true }).FillAsync(subject);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add activity", Exact = true }).ClickAsync();
        await ExpectPathAsync(page, detailsPath);
        foreach (var action in new[] { (Button: "Complete", Status: "Completed"), (Button: "Reopen", Status: "Open") })
        {
            var entry = page.Locator("main li").Filter(new() { HasText = subject });
            await FollowAsync(page, entry.GetByRole(AriaRole.Link, new() { Name = "Manage", Exact = true }));
            await page.GetByRole(AriaRole.Button, new() { Name = action.Button, Exact = true }).ClickAsync();
            await ExpectPathAsync(page, detailsPath);
            await Assertions.Expect(page.Locator("main li").Filter(new() { HasText = subject })).ToContainTextAsync(action.Status);
        }
    }

    private static async Task VerifyArchiveAsync(IPage page, string detailsPath)
    {
        foreach (var action in new[] { (Button: "Archive", Status: "Archived"), (Button: "Reactivate", Status: "Active") })
        {
            await FollowAsync(page, action.Button);
            await page.GetByRole(AriaRole.Button, new() { Name = action.Button, Exact = true }).ClickAsync();
            await ExpectPathAsync(page, detailsPath);
            await Assertions.Expect(page.Locator("main dl").GetByText(action.Status, new() { Exact = true })).ToBeVisibleAsync();
        }
    }

    private static Task FollowAsync(IPage page, string name) =>
        FollowAsync(page, page.GetByRole(AriaRole.Link, new() { Name = name, Exact = true }));

    private static Task ExpectPathAsync(IPage page, string path) =>
        Assertions.Expect(page).ToHaveURLAsync(new Uri(new Uri(page.Url), path).ToString(), new() { Timeout = 30_000 });

    private static async Task FollowAsync(IPage page, ILocator link)
    {
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        var href = await link.GetAttributeAsync("href");
        Assert.False(string.IsNullOrWhiteSpace(href));
        var target = new Uri(new Uri(page.Url), href);
        await link.ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(target.ToString(), new() { Timeout = 30_000 });
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
    }

    private static async Task VisitAsync(IPage page, string baseUrl, string path, string heading)
    {
        var response = await page.GotoAsync(new Uri(new Uri(baseUrl), path).ToString());
        Assert.NotNull(response);
        Assert.True(response.Ok, $"CRM navigation returned HTTP {response.Status}.");
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1, Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
    }

    private static async Task<JsonElement> GetJsonAsync(IPage page, string baseUrl, string path)
    {
        var response = await page.Context.APIRequest.GetAsync(new Uri(new Uri(baseUrl), path).ToString());
        Assert.True(response.Ok, $"CRM read returned HTTP {response.Status}.");
        using var document = JsonDocument.Parse(await response.TextAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(IPage page, string baseUrl, string path, object request)
    {
        var response = await page.Context.APIRequest.PostAsync(new Uri(new Uri(baseUrl), path).ToString(), new() { DataObject = request });
        Assert.True(response.Ok, $"CRM transition returned HTTP {response.Status}.");
        using var document = JsonDocument.Parse(await response.TextAsync());
        return document.RootElement.Clone();
    }

    [GeneratedRegex("/crm/leads/[^/]+/details$")]
    private static partial Regex LeadDetailsUrl();
}
