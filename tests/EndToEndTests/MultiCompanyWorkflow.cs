using System.Runtime.ExceptionServices;
using System.Text.Json;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Erp.EndToEndTests;

internal static class MultiCompanyWorkflow
{
    public static async Task VerifyAsync(IPage page, string baseUrl, string email, string password, string originalSku)
    {
        var initialCompanies = await CompaniesAsync(page.Context.APIRequest, baseUrl);
        var original = initialCompanies.Single(company => company.GetProperty("isCurrent").GetBoolean());
        var originalId = original.GetProperty("id").GetGuid();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var companyCode = $"BROWSER-{suffix}".ToUpperInvariant();
        Guid? createdCompanyId = null;
        Guid? createdUserId = null;
        Exception? workflowFailure = null;
        Exception? cleanupFailure = null;

        await using var otherSession = await page.Context.Browser!.NewContextAsync();
        var otherPage = await otherSession.NewPageAsync();
        await SignInAsync(otherPage, baseUrl, email, password);

        try
        {
            await page.GotoAsync(Url(baseUrl, "/organization"));
            await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add company", Exact = true }).ClickAsync();
            var editor = page.Locator(".company-editor");
            await editor.GetByLabel("Name", new() { Exact = false }).FillAsync($"Browser company {suffix}");
            await editor.GetByLabel("Company code", new() { Exact = false }).FillAsync(companyCode);
            await editor.GetByLabel("Base currency", new() { Exact = false }).FillAsync("USD");
            await editor.GetByLabel("Time zone", new() { Exact = false }).FillAsync("UTC");
            await editor.GetByRole(AriaRole.Button, new() { Name = "Save company" }).ClickAsync();
            await page.WaitForURLAsync("**/organization?created=true");
            await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
            var created = (await CompaniesAsync(page.Context.APIRequest, baseUrl)).Single(company => company.GetProperty("code").GetString() == companyCode);
            createdCompanyId = created.GetProperty("id").GetGuid();
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Active company", Exact = true })).ToBeVisibleAsync();
            Assert.Empty((await page.RunAxe()).Violations ?? []);

            await SwitchAsync(page, baseUrl, createdCompanyId.Value);
            var switched = await ContextAsync(page.Context.APIRequest, baseUrl);
            Assert.Equal(createdCompanyId, switched.GetProperty("companyId").GetProperty("value").GetGuid());
            // Another signed-in browser keeps its original company despite the persisted default changing.
            var otherContext = await ContextAsync(otherSession.APIRequest, baseUrl);
            Assert.Equal(originalId, otherContext.GetProperty("companyId").GetProperty("value").GetGuid());
            var products = await page.Context.APIRequest.GetAsync(Url(baseUrl, $"/api/v1/products?search={originalSku}"));
            Assert.True(products.Ok);
            using (var data = JsonDocument.Parse(await products.TextAsync())) Assert.Equal(0, data.RootElement.GetProperty("items").GetArrayLength());

            // A company's own product SKU can repeat in another company without crossing data boundaries.
            await page.GotoAsync(Url(baseUrl, "/products/new"));
            await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
            await page.Locator("label").Filter(new() { HasText = "SKU" }).Locator("input").FillAsync(originalSku);
            await page.Locator("label").Filter(new() { HasText = "Name" }).Locator("input").FillAsync("Company-isolated browser product");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save product" }).ClickAsync();
            await page.WaitForURLAsync("**/products/*/details");
            var isolatedProductId = page.Url.Split('/')[^2];
            await SwitchAsync(page, baseUrl, originalId);
            var foreignProduct = await page.Context.APIRequest.GetAsync(Url(baseUrl, $"/api/v1/products/{isolatedProductId}"));
            Assert.Equal(404, foreignProduct.Status);
            var originalProducts = await page.Context.APIRequest.GetAsync(Url(baseUrl, $"/api/v1/products?search={originalSku}"));
            using (var data = JsonDocument.Parse(await originalProducts.TextAsync())) Assert.Equal(1, data.RootElement.GetProperty("items").GetArrayLength());

            // Preview is read-only in the live environment. Destructive retention checks use isolated test databases.
            await page.GotoAsync(Url(baseUrl, "/administration/data-maintenance"));
            await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
            await page.GetByRole(AriaRole.Button, new() { Name = "Preview cleanup" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Cleanup preview for {original.GetProperty("name").GetString()}" })).ToBeVisibleAsync();
            Assert.Empty((await page.RunAxe()).Violations ?? []);

            await page.GotoAsync(Url(baseUrl, "/administration/roles"));
            await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add role", Exact = true }).ClickAsync();
            await page.GetByLabel("Role name", new() { Exact = false }).FillAsync($"Browser reader {suffix}");
            await page.GetByRole(AriaRole.Checkbox, new() { Name = "View organization context", Exact = true }).CheckAsync();
            await page.GetByRole(AriaRole.Checkbox, new() { Name = "View products", Exact = true }).CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save role", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = $"Browser reader {suffix} was saved." })).ToBeVisibleAsync();
            Assert.Empty((await page.RunAxe()).Violations ?? []);
            var userEmail = $"browser-{suffix}@example.test";
            var userPassword = $"Browser!7a{Guid.NewGuid():N}";
            await page.GotoAsync(Url(baseUrl, "/administration/users"));
            await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
            await page.GetByRole(AriaRole.Button, new() { Name = "New user", Exact = true }).ClickAsync();
            var accessEditor = page.GetByRole(AriaRole.Region, new() { Name = "User access editor", Exact = true });
            await accessEditor.GetByLabel("Email address", new() { Exact = true }).FillAsync(userEmail);
            await accessEditor.GetByLabel("Initial password", new() { Exact = true }).FillAsync(userPassword);
            await accessEditor.Locator("label.checkbox-field").Filter(new() { HasText = $"Browser reader {suffix}" }).Locator("input").CheckAsync();
            await accessEditor.GetByRole(AriaRole.Button, new() { Name = "Create user", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "User created." })).ToBeVisibleAsync();
            var usersResponse = await page.Context.APIRequest.GetAsync(Url(baseUrl, "/api/v1/platform/users"));
            Assert.True(usersResponse.Ok);
            using var userData = JsonDocument.Parse(await usersResponse.TextAsync());
            createdUserId = userData.RootElement.EnumerateArray().Single(user => user.GetProperty("email").GetString() == userEmail).GetProperty("userId").GetGuid();
            Assert.Empty((await page.RunAxe()).Violations ?? []);
            await using var readerSession = await page.Context.Browser.NewContextAsync();
            var readerPage = await readerSession.NewPageAsync();
            await SignInAsync(readerPage, baseUrl, userEmail, userPassword);
            var visibleCompanies = await CompaniesAsync(readerSession.APIRequest, baseUrl);
            Assert.Single(visibleCompanies);
            Assert.Equal(originalId, visibleCompanies[0].GetProperty("id").GetGuid());
            var forbidden = await readerSession.APIRequest.GetAsync(Url(baseUrl, "/api/v1/platform/users"));
            Assert.Equal(403, forbidden.Status);
            await readerPage.GotoAsync(Url(baseUrl, "/organization"));
            var invalidSwitch = readerSession.APIRequest.CreateFormData();
            invalidSwitch.Set("companyId", createdCompanyId.Value.ToString());
            invalidSwitch.Set("returnUrl", "/organization");
            invalidSwitch.Set("__RequestVerificationToken", await readerPage.Locator("input[name='__RequestVerificationToken']").First.InputValueAsync());
            var refused = await readerSession.APIRequest.PostAsync(Url(baseUrl, "/account/company/switch"), new() { Form = invalidSwitch });
            Assert.Equal(403, refused.Status);

            await accessEditor.GetByRole(AriaRole.Button, new() { Name = "Deactivate user", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "User deactivated." })).ToBeVisibleAsync();
            var revoked = await readerSession.APIRequest.GetAsync(Url(baseUrl, "/api/v1/platform/context"));
            Assert.Equal(401, revoked.Status);
            createdUserId = null;
        }
        catch (Exception exception)
        {
            workflowFailure = exception;
        }
        finally
        {
            try
            {
                await SwitchAsync(page, baseUrl, originalId);
                if (createdUserId is { } userId) await DisableUserAsync(page.Context.APIRequest, baseUrl, userId);
                if (createdCompanyId is { } companyId)
                {
                    var company = (await CompaniesAsync(page.Context.APIRequest, baseUrl)).Single(item => item.GetProperty("id").GetGuid() == companyId);
                    var result = await page.Context.APIRequest.PutAsync(Url(baseUrl, $"/api/v1/platform/companies/{companyId}"), new()
                    {
                        DataObject = new { name = company.GetProperty("name").GetString(), code = company.GetProperty("code").GetString(), timeZoneId = company.GetProperty("timeZoneId").GetString(), isActive = false, expectedVersion = company.GetProperty("version").GetString() }
                    });
                    Assert.True(result.Ok, $"Test company deactivation returned {result.Status}.");
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
        }

        if (workflowFailure is not null && cleanupFailure is not null)
            throw new AggregateException("The company workflow and its cleanup both failed.", workflowFailure, cleanupFailure);
        if (workflowFailure is not null) ExceptionDispatchInfo.Capture(workflowFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private static async Task DisableUserAsync(IAPIRequestContext request, string baseUrl, Guid userId)
    {
        var response = await request.GetAsync(Url(baseUrl, "/api/v1/platform/users"));
        Assert.True(response.Ok);
        using var users = JsonDocument.Parse(await response.TextAsync());
        var user = users.RootElement.EnumerateArray().Single(item => item.GetProperty("userId").GetGuid() == userId);
        var disabled = await request.PutAsync(Url(baseUrl, $"/api/v1/platform/users/{userId}/active"), new()
        {
            DataObject = new { isActive = false, concurrencyStamp = user.GetProperty("concurrencyStamp").GetString() }
        });
        Assert.True(disabled.Ok, $"Test account disable returned {disabled.Status}.");
    }

    private static async Task SwitchAsync(IPage page, string baseUrl, Guid companyId)
    {
        var response = await page.GotoAsync(Url(baseUrl, "/organization"));
        Assert.NotNull(response);
        Assert.True(response.Ok, $"Company navigation returned HTTP {response.Status}.");
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        var selector = page.GetByRole(AriaRole.Combobox, new() { Name = "Active company", Exact = true });
        if (!await selector.IsVisibleAsync()) return;
        await selector.SelectOptionAsync(companyId.ToString());
        var destination = $"/organization?switched={Guid.NewGuid():N}";
        await page.Locator(".company-switcher input[name='returnUrl']").EvaluateAsync("(element, value) => element.value = value", destination);
        await page.Locator(".company-switcher").GetByRole(AriaRole.Button, new() { Name = "Switch", Exact = true }).ClickAsync();
        await page.WaitForURLAsync($"**{destination}");
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        await Assertions.Expect(selector).ToHaveValueAsync(companyId.ToString());
    }

    private static async Task SignInAsync(IPage page, string baseUrl, string email, string password)
    {
        await page.GotoAsync(Url(baseUrl, "/account/login"));
        var form = page.Context.APIRequest.CreateFormData();
        form.Set("email", email); form.Set("password", password);
        form.Set("__RequestVerificationToken", await page.Locator("input[name='__RequestVerificationToken']").InputValueAsync());
        var response = await page.Context.APIRequest.PostAsync(Url(baseUrl, "/account/login"), new() { Form = form });
        Assert.True(response.Ok);
        await page.GotoAsync(Url(baseUrl, "/organization"));
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Organization and companies", Level = 1 })).ToBeVisibleAsync();
    }

    private static async Task<JsonElement[]> CompaniesAsync(IAPIRequestContext request, string baseUrl)
    {
        var response = await request.GetAsync(Url(baseUrl, "/api/v1/platform/companies"));
        Assert.True(response.Ok);
        using var document = JsonDocument.Parse(await response.TextAsync());
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static async Task<JsonElement> ContextAsync(IAPIRequestContext request, string baseUrl)
    {
        var response = await request.GetAsync(Url(baseUrl, "/api/v1/platform/context"));
        Assert.True(response.Ok);
        using var document = JsonDocument.Parse(await response.TextAsync());
        return document.RootElement.Clone();
    }

    private static string Url(string baseUrl, string path) => new Uri(new Uri(baseUrl), path).ToString();
}
