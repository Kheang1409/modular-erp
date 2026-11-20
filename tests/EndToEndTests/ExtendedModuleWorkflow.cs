using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;

namespace Erp.EndToEndTests;

internal static class ExtendedModuleWorkflow
{
    public static async Task VerifyAsync(IPage page, string baseUrl, string rawMaterialSku)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var employee = await VerifyHcmAsync(page, baseUrl, suffix);
        await VerifyManufacturingAsync(page, baseUrl, rawMaterialSku, suffix);
        await VerifyProjectsAsync(page, baseUrl, suffix);
        await VerifySupplierSourcingAsync(page, baseUrl, suffix);

        foreach (var route in new[]
        {
            (Path: "/hcm/benefits", Heading: "Employee benefits"),
            (Path: "/hcm/training", Heading: "Training records"),
            (Path: "/hcm/reviews", Heading: "Performance reviews"),
            (Path: "/hcm/payroll", Heading: "Payroll"),
            (Path: "/manufacturing/capacity", Heading: "Production capacity")
        })
        {
            await VisitAsync(page, baseUrl, route.Path, route.Heading);
            await VerifyResponsiveAsync(page, route.Heading);
        }

        Assert.NotEqual(Guid.Empty, employee);
    }

    private static async Task<Guid> VerifyHcmAsync(IPage page, string baseUrl, string suffix)
    {
        var number = $"QA-2026-EMP-{suffix}";
        var fullName = $"QA-2026-Employee {suffix}";
        var email = $"QA-2026-employee-{suffix}@qa-2026.example.test";
        var hireDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        await VisitAsync(page, baseUrl, "/hcm/people", "Employees");
        await page.GetByLabel("Employee number", new() { Exact = true }).FillAsync(number);
        await page.GetByLabel("Full name", new() { Exact = true }).FillAsync(fullName);
        await page.GetByLabel("Email (optional)", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Hire date", new() { Exact = true }).FillAsync(Date(hireDate));
        await page.GetByRole(AriaRole.Button, new() { Name = "Add employee", Exact = true }).ClickAsync();

        var employeeResults = await GetAsync(page, baseUrl,
            $"/api/v1/hcm/employees?search={Uri.EscapeDataString(number)}");
        var matchingEmployees = employeeResults.EnumerateArray()
            .Where(item => item.GetProperty("employeeNumber").GetString() == number)
            .ToArray();
        var uiState = await page.EvaluateAsync<string>("() => JSON.stringify({ url: location.href, interactive: document.querySelector('.shell')?.dataset.interactive, alerts: [...document.querySelectorAll('.notice-error[role=alert]')].map(item => item.innerText), rows: [...document.querySelectorAll('tbody tr')].map(item => item.innerText), forms: [...document.forms].map(form => ({ action: form.action, method: form.method, fields: [...form.querySelectorAll('input')].map(input => ({ name: input.name, value: input.value, valid: input.validity.valid })) })) })");
        Assert.True(matchingEmployees.Length == 1,
            $"Expected one persisted HCM employee; API response: {employeeResults.GetRawText()}; UI state: {uiState}");
        var employee = matchingEmployees[0];
        var employeeId = employee.GetProperty("id").GetGuid();
        Assert.Equal(fullName, Text(employee, "fullName"));
        Assert.Equal(email, Text(employee, "email"));
        Assert.True(IsEnum(employee.GetProperty("status"), "Active", 0));
        var employeeRow = page.Locator("tbody tr").Filter(new() { HasText = number });
        await Assertions.Expect(employeeRow).ToContainTextAsync(fullName);
        await employeeRow.GetByRole(AriaRole.Button, new() { Name = "Deactivate", Exact = true }).ClickAsync();
        var inactive = await GetAsync(page, baseUrl, $"/api/v1/hcm/employees/{employeeId}");
        Assert.True(IsEnum(inactive.GetProperty("status"), "Inactive", 1));
        await employeeRow.GetByRole(AriaRole.Button, new() { Name = "Reactivate", Exact = true }).ClickAsync();
        var active = await GetAsync(page, baseUrl, $"/api/v1/hcm/employees/{employeeId}");
        Assert.True(IsEnum(active.GetProperty("status"), "Active", 0));

        var export = await page.Context.APIRequest.GetAsync(Url(baseUrl,
            $"/api/v1/hcm/employees/export?search={Uri.EscapeDataString(number)}"));
        Assert.True(export.Ok, $"HCM employee export returned HTTP {export.Status}.");
        Assert.Contains(number, await export.TextAsync(), StringComparison.Ordinal);
        Assert.StartsWith("text/csv", export.Headers["content-type"], StringComparison.OrdinalIgnoreCase);

        var leaveStart = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
        var leaveEnd = leaveStart.AddDays(1);
        var leaveReason = $"QA-2026-Leave {suffix}";
        await VisitAsync(page, baseUrl, "/hcm/leave", "Leave requests");
        await page.GetByLabel("Employee ID", new() { Exact = true }).FillAsync(employeeId.ToString());
        await page.GetByLabel("Start date", new() { Exact = true }).FillAsync(Date(leaveStart));
        await page.GetByLabel("End date", new() { Exact = true }).FillAsync(Date(leaveEnd));
        await page.GetByLabel("Reason", new() { Exact = true }).FillAsync(leaveReason);
        await page.GetByRole(AriaRole.Button, new() { Name = "Submit request", Exact = true }).ClickAsync();
        var leaveRow = page.Locator("tbody tr").Filter(new() { HasText = leaveReason });
        await Assertions.Expect(leaveRow).ToContainTextAsync("Pending");
        var pendingLeave = await SingleByAsync(await GetAsync(page, baseUrl,
            $"/api/v1/hcm/leave-requests?employeeId={employeeId}"), "reason", leaveReason);
        Assert.True(IsEnum(pendingLeave.GetProperty("status"), "Pending", 0));
        await leaveRow.GetByRole(AriaRole.Button, new() { Name = "Approve", Exact = true }).ClickAsync();
        var approvedLeave = await SingleByAsync(await GetAsync(page, baseUrl,
            $"/api/v1/hcm/leave-requests?employeeId={employeeId}"), "reason", leaveReason);
        Assert.True(IsEnum(approvedLeave.GetProperty("status"), "Approved", 1));
        await VerifyResponsiveAsync(page, "Leave requests");

        var workDate = DateOnly.FromDateTime(DateTime.UtcNow);
        await VisitAsync(page, baseUrl, "/hcm/attendance", "Attendance");
        await page.GetByLabel("Employee ID", new() { Exact = true }).FillAsync(employeeId.ToString());
        await page.GetByLabel("Work date", new() { Exact = true }).FillAsync(Date(workDate));
        await page.GetByRole(AriaRole.Button, new() { Name = "Record check-in", Exact = true }).ClickAsync();
        var attendanceRow = page.Locator("tbody tr").Filter(new() { HasText = fullName }).Filter(new() { HasText = Date(workDate) });
        await Assertions.Expect(attendanceRow).ToBeVisibleAsync();
        await attendanceRow.GetByRole(AriaRole.Button, new() { Name = "Check out", Exact = true }).ClickAsync();
        var attendance = await SingleByAsync(await GetAsync(page, baseUrl,
            $"/api/v1/hcm/attendance?employeeId={employeeId}&workDate={Date(workDate)}"), "workDate", Date(workDate));
        Assert.NotEqual(JsonValueKind.Null, attendance.GetProperty("checkedInAtUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, attendance.GetProperty("checkedOutAtUtc").ValueKind);
        await VerifyResponsiveAsync(page, "Attendance");
        return employeeId;
    }

    private static async Task VerifyManufacturingAsync(IPage page, string baseUrl, string rawMaterialSku, string suffix)
    {
        var rawSearch = await GetAsync(page, baseUrl, $"/api/v1/products/?search={Uri.EscapeDataString(rawMaterialSku)}");
        var rawProduct = SingleBy(rawSearch.GetProperty("items"), "sku", rawMaterialSku);
        var rawProductId = rawProduct.GetProperty("id").GetGuid();
        var stock = await GetAsync(page, baseUrl,
            $"/api/v1/inventory/balances?productId={rawProductId}&includeZero=true&page=1&pageSize=100");
        var stockedLocation = stock.GetProperty("items").EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("available").GetDecimal() >= 2m);
        Assert.NotEqual(default, stockedLocation);
        var locationId = stockedLocation.GetProperty("locationId").GetGuid();

        var finishedSku = $"QA-2026-FG-{suffix}";
        var createdProduct = await PostAsync(page, baseUrl, "/api/v1/products/", new
        {
            type = rawProduct.GetProperty("type").GetInt32(),
            sku = finishedSku,
            name = $"QA-2026-Finished good {suffix}",
            salePrice = rawProduct.GetProperty("salePrice").GetDecimal(),
            cost = rawProduct.GetProperty("cost").GetDecimal(),
            currencyCode = Text(rawProduct, "currencyCode"),
            salesUnitCode = Text(rawProduct, "salesUnitCode"),
            purchaseUnitCode = Text(rawProduct, "purchaseUnitCode"),
            tracksInventory = true
        });
        var finishedProductId = createdProduct.GetProperty("id").GetGuid();
        var billNumber = $"QA-2026-BOM-{suffix}";
        await VisitAsync(page, baseUrl, "/manufacturing/bills", "Bills of materials");
        await page.GetByLabel("Bill number", new() { Exact = true }).FillAsync(billNumber);
        await page.GetByLabel("Finished product", new() { Exact = true }).SelectOptionAsync(finishedProductId.ToString());
        await page.GetByLabel("Revision", new() { Exact = true }).FillAsync("A");
        await page.GetByLabel("Output quantity", new() { Exact = true }).FillAsync("1");
        await page.GetByLabel("Notes", new() { Exact = true }).FillAsync($"QA-2026-Production specification {suffix}");
        await page.GetByLabel("Component", new() { Exact = true }).SelectOptionAsync(rawProductId.ToString());
        await page.GetByLabel("Quantity per output", new() { Exact = true }).FillAsync("2");
        await page.GetByLabel("Standard unit cost", new() { Exact = true }).FillAsync("12.5");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft bill", Exact = true }).ClickAsync();
        var billRow = page.Locator("tbody tr").Filter(new() { HasText = billNumber });
        await Assertions.Expect(billRow).ToBeVisibleAsync();
        await billRow.GetByRole(AriaRole.Button, new() { Name = "Activate", Exact = true }).ClickAsync();
        var bill = SingleBy(await GetAsync(page, baseUrl,
                $"/api/v1/manufacturing/bills-of-material?search={Uri.EscapeDataString(billNumber)}"), "number", billNumber);
        Assert.Equal("Active", Text(bill, "state"));
        Assert.Equal(2m, Assert.Single(bill.GetProperty("components").EnumerateArray()).GetProperty("quantity").GetDecimal());

        var workOrderNumber = $"QA-2026-WO-{suffix}";
        var start = DateOnly.FromDateTime(DateTime.UtcNow);
        var end = start.AddDays(2);
        await VisitAsync(page, baseUrl, "/manufacturing/work-orders", "Work orders");
        await page.GetByLabel("Order number", new() { Exact = true }).FillAsync(workOrderNumber);
        await page.GetByLabel("Active bill", new() { Exact = true }).SelectOptionAsync(bill.GetProperty("id").GetGuid().ToString());
        await page.GetByLabel("Quantity", new() { Exact = true }).FillAsync("1");
        await page.GetByLabel("Planned labor hours", new() { Exact = true }).FillAsync("2");
        await page.GetByLabel("Cost currency", new() { Exact = true }).FillAsync("USD");
        await page.GetByLabel("Inventory location", new() { Exact = true }).SelectOptionAsync(locationId.ToString());
        await page.GetByLabel("Planned start", new() { Exact = true }).FillAsync(Date(start));
        await page.GetByLabel("Planned end", new() { Exact = true }).FillAsync(Date(end));
        await page.GetByLabel("Notes", new() { Exact = true }).FillAsync($"QA-2026-Work order {suffix}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create work order", Exact = true }).ClickAsync();
        var order = SingleBy(await GetAsync(page, baseUrl, "/api/v1/manufacturing/work-orders"), "number", workOrderNumber);
        Assert.Equal("Draft", Text(order, "state"));
        Assert.Equal(25m, order.GetProperty("plannedMaterialCost").GetDecimal());

        var orderRow = page.Locator("tbody tr").Filter(new() { HasText = workOrderNumber });
        await orderRow.GetByRole(AriaRole.Button, new() { Name = workOrderNumber, Exact = true }).ClickAsync();
        await orderRow.GetByRole(AriaRole.Button, new() { Name = "Release", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Work order moved to Released." })).ToBeVisibleAsync();
        await orderRow.GetByRole(AriaRole.Button, new() { Name = "Start", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Work order moved to InProgress." })).ToBeVisibleAsync();
        await page.GetByLabel("Actual output quantity", new() { Exact = true }).FillAsync("1");
        await page.GetByLabel("Actual labor hours", new() { Exact = true }).FillAsync("2");
        await page.GetByLabel("Actual material cost", new() { Exact = true }).FillAsync("25");
        await page.GetByLabel("Actual labor cost", new() { Exact = true }).FillAsync("15");
        await page.GetByLabel("Actual overhead cost", new() { Exact = true }).FillAsync("5");
        await page.GetByRole(AriaRole.Button, new() { Name = "Complete production", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Production completed." })).ToBeVisibleAsync();
        var completed = SingleBy(await GetAsync(page, baseUrl, "/api/v1/manufacturing/work-orders"), "number", workOrderNumber);
        var workOrderId = completed.GetProperty("id").GetGuid();
        Assert.Equal("Completed", Text(completed, "state"));
        Assert.Equal(1m, completed.GetProperty("completedQuantity").GetDecimal());
        Assert.Equal(45m, completed.GetProperty("totalProductionCost").GetDecimal());
        var history = await GetAsync(page, baseUrl, $"/api/v1/manufacturing/work-orders/{workOrderId}/status-history");
        Assert.True(history.GetArrayLength() >= 3);
        var rawBalance = await GetAsync(page, baseUrl, $"/api/v1/inventory/balances/{rawProductId}/{locationId}");
        var finishedBalance = await GetAsync(page, baseUrl, $"/api/v1/inventory/balances/{finishedProductId}/{locationId}");
        Assert.Equal(3m, rawBalance.GetProperty("onHand").GetDecimal());
        Assert.Equal(0m, rawBalance.GetProperty("reserved").GetDecimal());
        Assert.Equal(1m, finishedBalance.GetProperty("onHand").GetDecimal());

        var inspectionNumber = $"QA-2026-QC-{suffix}";
        await page.GetByLabel("Inspection number", new() { Exact = true }).FillAsync(inspectionNumber);
        await page.GetByLabel("Sample quantity", new() { Exact = true }).FillAsync("1");
        await page.GetByLabel("Decision", new() { Exact = true }).SelectOptionAsync("Accepted");
        await page.GetByLabel("Inspector", new() { Exact = true }).FillAsync("QA-2026-Quality team");
        await page.GetByLabel("Notes", new() { Exact = true }).FillAsync($"QA-2026-Inspection {suffix}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save inspection", Exact = true }).ClickAsync();
        var inspection = SingleBy(await GetAsync(page, baseUrl,
            $"/api/v1/manufacturing/work-orders/{workOrderId}/inspections"), "number", inspectionNumber);
        Assert.Equal("Accepted", Text(inspection, "decision"));
        await VerifyResponsiveAsync(page, "Work orders");
    }

    private static async Task VerifyProjectsAsync(IPage page, string baseUrl, string suffix)
    {
        var projectName = $"QA-2026-ERP delivery {suffix}";
        var description = $"QA-2026-Cross-module delivery plan {suffix}";
        var start = DateOnly.FromDateTime(DateTime.UtcNow);
        var end = start.AddDays(30);
        await VisitAsync(page, baseUrl, "/projects", "Projects");
        await page.GetByLabel("Project name", new() { Exact = true }).FillAsync(projectName);
        await page.GetByLabel("Status", new() { Exact = true }).SelectOptionAsync("Planned");
        await page.GetByLabel("Start date", new() { Exact = true }).FillAsync(Date(start));
        await page.GetByLabel("Target end date", new() { Exact = true }).FillAsync(Date(end));
        await page.GetByLabel("Project brief", new() { Exact = true }).FillAsync(description);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create project", Exact = true }).ClickAsync();
        var project = SingleBy(await GetAsync(page, baseUrl, "/api/v1/projects/"), "name", projectName);
        var projectId = project.GetProperty("id").GetGuid();
        Assert.True(IsEnum(project.GetProperty("status"), "Planned", 0));
        var projectCard = page.Locator(".project-card").Filter(new() { HasText = projectName });
        await Assertions.Expect(projectCard).ToBeVisibleAsync();
        await page.GetByLabel("Search projects", new() { Exact = true }).FillAsync(projectName);
        await Assertions.Expect(page.Locator(".project-card")).ToHaveCountAsync(1);
        await page.GetByLabel("Search projects", new() { Exact = true }).FillAsync("QA-2026-no-such-project");
        await Assertions.Expect(page.Locator(".project-card")).ToHaveCountAsync(0);
        await page.GetByLabel("Search projects", new() { Exact = true }).FillAsync(projectName);
        await projectCard.GetByRole(AriaRole.Button, new() { Name = "Start", Exact = true }).ClickAsync();
        var activeProject = await GetAsync(page, baseUrl, $"/api/v1/projects/{projectId}");
        Assert.True(IsEnum(activeProject.GetProperty("status"), "Active", 1));

        var milestoneName = $"QA-2026-Milestone {suffix}";
        await VisitAsync(page, baseUrl, "/projects/milestones", "Milestones");
        await page.GetByRole(AriaRole.Combobox).First.SelectOptionAsync(projectId.ToString());
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync(milestoneName);
        await page.GetByLabel("Description", new() { Exact = true }).FillAsync($"QA-2026-Delivery checkpoint {suffix}");
        await page.GetByLabel("Due date", new() { Exact = true }).FillAsync(Date(end));
        await page.GetByRole(AriaRole.Button, new() { Name = "Add milestone", Exact = true }).ClickAsync();
        var milestone = SingleBy(await GetAsync(page, baseUrl, $"/api/v1/projects/{projectId}/milestones/"), "name", milestoneName);
        var milestoneId = milestone.GetProperty("id").GetGuid();
        await Assertions.Expect(page.Locator("tbody tr").Filter(new() { HasText = milestoneName })).ToBeVisibleAsync();

        var taskTitle = $"QA-2026-Implement delivery milestone {suffix}";
        await VisitAsync(page, baseUrl, "/projects/tasks", "Tasks");
        await page.GetByRole(AriaRole.Combobox).First.SelectOptionAsync(projectId.ToString());
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync(taskTitle);
        await page.GetByLabel("Description", new() { Exact = true }).FillAsync($"QA-2026-Task for {milestoneName}");
        await page.GetByLabel("Milestone", new() { Exact = true }).SelectOptionAsync(milestoneId.ToString());
        await page.GetByLabel("Due date", new() { Exact = true }).FillAsync(Date(end));
        await page.GetByRole(AriaRole.Button, new() { Name = "Add task", Exact = true }).ClickAsync();
        var task = SingleBy(await GetAsync(page, baseUrl, $"/api/v1/projects/{projectId}/tasks/"), "title", taskTitle);
        var taskId = task.GetProperty("id").GetGuid();
        var taskRow = page.Locator("tbody tr").Filter(new() { HasText = taskTitle });
        await taskRow.GetByRole(AriaRole.Button, new() { Name = "Start / resume", Exact = true }).ClickAsync();
        await taskRow.GetByRole(AriaRole.Button, new() { Name = "Complete", Exact = true }).ClickAsync();
        var completedTask = SingleBy(await GetAsync(page, baseUrl, $"/api/v1/projects/{projectId}/tasks/"), "id", taskId.ToString());
        Assert.True(IsEnum(completedTask.GetProperty("status"), "Completed", 2));

        var workDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var timeDescription = $"QA-2026-Billable design work {suffix}";
        await VisitAsync(page, baseUrl, "/projects/time", "Project time entries");
        await page.GetByRole(AriaRole.Combobox).First.SelectOptionAsync(projectId.ToString());
        await page.GetByLabel("Task", new() { Exact = true }).SelectOptionAsync(taskId.ToString());
        await page.GetByLabel("Work date", new() { Exact = true }).FillAsync(Date(workDate));
        await page.GetByLabel("Hours", new() { Exact = true }).FillAsync("2.5");
        await page.GetByLabel("Billable", new() { Exact = true }).CheckAsync();
        await page.GetByLabel("Description", new() { Exact = true }).FillAsync(timeDescription);
        await page.GetByRole(AriaRole.Button, new() { Name = "Record hours", Exact = true }).ClickAsync();
        var timeEntry = SingleBy(await GetAsync(page, baseUrl,
            $"/api/v1/projects/{projectId}/time-entries/?from={Date(workDate)}"), "description", timeDescription);
        Assert.Equal(2.5m, timeEntry.GetProperty("hours").GetDecimal());
        Assert.True(timeEntry.GetProperty("isBillable").GetBoolean());
        Assert.Equal(taskId, timeEntry.GetProperty("taskId").GetGuid());
        await Assertions.Expect(page.Locator("tbody tr").Filter(new() { HasText = timeDescription })).ToBeVisibleAsync();
        await VerifyResponsiveAsync(page, "Project time entries");
    }

    private static async Task VerifySupplierSourcingAsync(IPage page, string baseUrl, string suffix)
    {
        var supplierName = $"QA-2026-Supplier {suffix}";
        var supplier = await PostAsync(page, baseUrl, "/api/v1/contacts/", new
        {
            kind = 1,
            displayName = supplierName,
            organizationName = supplierName,
            isSupplier = true,
            taxNumber = $"QA-2026-TAX-{suffix}",
            billingAddress = new { line1 = "10 QA Test Way", city = "Test City", postalCode = "12345", countryCode = "US" }
        });
        var supplierId = supplier.GetProperty("id").GetGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var contractNumber = $"QA-2026-CONTRACT-{suffix}";
        var contractTitle = $"QA-2026-Supply agreement {suffix}";
        await VisitAsync(page, baseUrl, "/purchasing/suppliers/contracts", "Supplier contracts");
        await page.GetByLabel("Supplier ID", new() { Exact = true }).FillAsync(supplierId.ToString());
        await page.GetByLabel("Contract number", new() { Exact = true }).FillAsync(contractNumber);
        await page.GetByLabel("Title", new() { Exact = true }).FillAsync(contractTitle);
        await page.GetByLabel("Start date", new() { Exact = true }).FillAsync(Date(today));
        await page.GetByLabel("End date", new() { Exact = true }).FillAsync(Date(today.AddYears(1)));
        await page.GetByLabel("Contract value", new() { Exact = true }).FillAsync("15000");
        await page.GetByLabel("Currency code", new() { Exact = true }).FillAsync("USD");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create draft", Exact = true }).ClickAsync();
        var contractRow = page.Locator("tbody tr").Filter(new() { HasText = contractNumber });
        await Assertions.Expect(contractRow).ToBeVisibleAsync();
        var contract = SingleBy(await GetAsync(page, baseUrl,
            "/api/v1/purchasing/suppliers/contracts/"), "contractNumber", contractNumber);
        Assert.True(IsEnum(contract.GetProperty("state"), "Draft", 0));
        await contractRow.GetByRole(AriaRole.Button, new() { Name = "Activate", Exact = true }).ClickAsync();
        contract = SingleBy(await GetAsync(page, baseUrl,
            "/api/v1/purchasing/suppliers/contracts/"), "contractNumber", contractNumber);
        Assert.True(IsEnum(contract.GetProperty("state"), "Active", 1));

        var notes = $"QA-2026-Evaluation {suffix}";
        await VisitAsync(page, baseUrl, "/purchasing/suppliers/evaluations", "Supplier evaluations");
        await page.GetByLabel("Supplier ID", new() { Exact = true }).FillAsync(supplierId.ToString());
        await page.GetByLabel("Assessment date", new() { Exact = true }).FillAsync(Date(today));
        var scores = page.Locator("main input[type='number']");
        await scores.Nth(0).FillAsync("6");
        Assert.True(await scores.Nth(0).EvaluateAsync<bool>("element => element.validity.rangeOverflow"),
            "Quality scores above five should fail browser validation.");
        await scores.Nth(0).FillAsync("5");
        await scores.Nth(1).FillAsync("4");
        await scores.Nth(2).FillAsync("5");
        await page.GetByLabel("Notes", new() { Exact = true }).FillAsync(notes);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save assessment", Exact = true }).ClickAsync();
        var evaluation = SingleBy(await GetAsync(page, baseUrl,
            "/api/v1/purchasing/suppliers/evaluations/"), "notes", notes);
        Assert.Equal(supplierId, evaluation.GetProperty("supplierId").GetGuid());
        Assert.Equal(5, evaluation.GetProperty("qualityScore").GetInt32());
        Assert.Equal(4, evaluation.GetProperty("deliveryScore").GetInt32());
        Assert.Equal(5, evaluation.GetProperty("responsivenessScore").GetInt32());
        Assert.Equal(4.67m, evaluation.GetProperty("overallScore").GetDecimal());
        await Assertions.Expect(page.Locator("tbody tr").Filter(new() { HasText = notes })).ToBeVisibleAsync();
        await VerifyResponsiveAsync(page, "Supplier evaluations");
    }

    private static async Task VisitAsync(IPage page, string baseUrl, string path, string heading)
    {
        await page.GotoAsync(Url(baseUrl, path));
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading,
            new() { Name = heading, Level = 1, Exact = true })).ToBeVisibleAsync();
        var errors = await page.Locator(".error-panel, main [role='alert']").AllTextContentsAsync();
        Assert.True(errors.Count == 0,
            $"{path} displayed an error: {string.Join(" | ", errors.Select(error => error.Trim()).Where(error => error.Length > 0))}");
    }

    private static async Task VerifyResponsiveAsync(IPage page, string context)
    {
        foreach (var width in new[] { 1440, 390 })
        {
            await page.SetViewportSizeAsync(width, 900);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"),
                $"{context} overflows at viewport width {width}.");
        }
        await page.SetViewportSizeAsync(1440, 1000);
    }

    private static async Task<JsonElement> GetAsync(IPage page, string baseUrl, string path)
    {
        var response = await page.Context.APIRequest.GetAsync(Url(baseUrl, path));
        Assert.True(response.Ok, $"GET {path} returned HTTP {response.Status}.");
        using var json = JsonDocument.Parse(await response.TextAsync());
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> PostAsync(IPage page, string baseUrl, string path, object request)
    {
        var response = await page.Context.APIRequest.PostAsync(Url(baseUrl, path), new() { DataObject = request });
        Assert.True(response.Ok, $"POST {path} returned HTTP {response.Status}.");
        using var json = JsonDocument.Parse(await response.TextAsync());
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> SingleByAsync(JsonElement values, string property, string expected) =>
        SingleBy(values, property, expected);

    private static JsonElement SingleBy(JsonElement values, string property, string expected) =>
        Assert.Single(values.EnumerateArray(), item =>
            item.GetProperty(property).ValueKind == JsonValueKind.String && item.GetProperty(property).GetString() == expected);

    private static bool IsEnum(JsonElement value, string name, int numeric) => value.ValueKind switch
    {
        JsonValueKind.String => string.Equals(value.GetString(), name, StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Number => value.GetInt32() == numeric,
        _ => false
    };

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Url(string baseUrl, string path) => new Uri(new Uri(baseUrl), path).ToString();
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;
}
