using Erp.Abstractions.Modules;
using Erp.Modules.Hcm.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using System.Globalization;
using System.Text;

namespace Erp.Modules.Hcm;

internal static class HcmEndpoints
{
    public static void MapHcmEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/hcm").WithTags("Human Capital Management");
        group.MapGet("/employees", ListEmployees)
            .WithName("ListHcmEmployees").WithSummary("List employee records")
            .WithDescription("Returns at most 200 employees from the active tenant and company.")
            .Produces<IReadOnlyList<EmployeeDto>>().RequirePermission(HcmPermissions.Read);
        group.MapGet("/employees/{id:guid}", GetEmployee)
            .WithName("GetHcmEmployee").WithSummary("Get an employee record")
            .WithDescription("Returns an employee visible to the active tenant and company.")
            .Produces<EmployeeDto>().Produces(StatusCodes.Status404NotFound)
            .RequirePermission(HcmPermissions.Read);
        group.MapPost("/employees", CreateEmployee)
            .WithName("CreateHcmEmployee").WithSummary("Create an employee record")
            .WithDescription("Creates an active employee in the current company.")
            .Produces<EmployeeDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.Manage);
        group.MapPut("/employees/{id:guid}", UpdateEmployee)
            .WithName("UpdateHcmEmployee").WithSummary("Update an employee record")
            .WithDescription("Updates the name, email, hire date, and status of an employee.")
            .Produces<EmployeeDto>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.Manage);
        group.MapGet("/employees/template", EmployeeTemplate).RequirePermission(HcmPermissions.Manage);
        group.MapGet("/employees/export", ExportEmployees).RequirePermission(HcmPermissions.Read);
        group.MapPost("/employees/import", ImportEmployees).Accepts<IFormFile>("multipart/form-data")
            .WithName("ImportHcmEmployees").RequirePermission(HcmPermissions.Manage);
        group.MapGet("/leave-requests", ListLeaveRequests)
            .WithName("ListHcmLeaveRequests").WithSummary("List leave requests")
            .WithDescription("Returns leave requests visible to the active tenant and company, optionally filtered by employee.")
            .Produces<IReadOnlyList<LeaveRequestDto>>().RequirePermission(HcmPermissions.Read);
        group.MapGet("/leave-requests/{id:guid}", GetLeaveRequest)
            .WithName("GetHcmLeaveRequest").WithSummary("Get a leave request")
            .WithDescription("Returns one leave request visible to the active tenant and company.")
            .Produces<LeaveRequestDto>().Produces(StatusCodes.Status404NotFound)
            .RequirePermission(HcmPermissions.Read);
        group.MapPost("/leave-requests", SubmitLeaveRequest)
            .WithName("SubmitHcmLeaveRequest").WithSummary("Submit a leave request")
            .WithDescription("Submits a leave request for an active employee.")
            .Produces<LeaveRequestDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.Manage);
        group.MapPost("/leave-requests/{id:guid}/decision", DecideLeaveRequest)
            .WithName("DecideHcmLeaveRequest").WithSummary("Approve or reject a pending leave request")
            .WithDescription("Records an approval or rejection for a pending leave request.")
            .Produces<LeaveRequestDto>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.ApproveLeave);
        group.MapGet("/attendance", ListAttendance)
            .WithName("ListHcmAttendance").WithSummary("List attendance records")
            .WithDescription("Returns attendance visible to the active tenant and company.")
            .Produces<IReadOnlyList<AttendanceRecordDto>>().RequirePermission(HcmPermissions.Read);
        group.MapGet("/attendance/{id:guid}", GetAttendance)
            .WithName("GetHcmAttendance").WithSummary("Get an attendance record")
            .WithDescription("Returns one attendance record visible to the active tenant and company.")
            .Produces<AttendanceRecordDto>().Produces(StatusCodes.Status404NotFound)
            .RequirePermission(HcmPermissions.Read);
        group.MapPost("/attendance/check-in", CheckIn)
            .WithName("CheckInHcmEmployee").WithSummary("Record employee check-in")
            .WithDescription("Creates the daily attendance record for an active employee.")
            .Produces<AttendanceRecordDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.Manage);
        group.MapPost("/attendance/{id:guid}/check-out", CheckOut)
            .WithName("CheckOutHcmEmployee").WithSummary("Record employee check-out")
            .WithDescription("Completes an open attendance record with a UTC check-out time.")
            .Produces<AttendanceRecordDto>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.Manage);
        group.MapPost("/attendance/{id:guid}/finalize", FinalizeAttendance)
            .WithName("FinalizeHcmAttendance").WithSummary("Approve finalized attendance and overtime")
            .Produces<AttendanceRecordDto>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict).RequirePermission(HcmPermissions.AttendanceApprove);
        group.MapPost("/attendance/dispositions", RecordAttendanceDisposition)
            .WithName("RecordHcmAttendanceDisposition").WithSummary("Record an approved leave, absence, or holiday day")
            .Produces<AttendanceRecordDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict).RequirePermission(HcmPermissions.AttendanceApprove);
        group.MapGet("/employee-benefits", ListBenefits)
            .WithName("ListHcmBenefits").WithSummary("List employee benefit enrollments")
            .WithDescription("Returns benefit enrollment records visible to the active tenant and company.")
            .Produces<IReadOnlyList<EmployeeBenefitDto>>().RequirePermission(HcmPermissions.BenefitsRead);
        group.MapGet("/employee-benefits/{id:guid}", GetBenefit)
            .WithName("GetHcmBenefit").WithSummary("Get an employee benefit enrollment")
            .WithDescription("Returns one benefit enrollment visible to the active tenant and company.")
            .Produces<EmployeeBenefitDto>().Produces(StatusCodes.Status404NotFound)
            .RequirePermission(HcmPermissions.BenefitsRead);
        group.MapPost("/employee-benefits", CreateBenefit)
            .WithName("CreateHcmBenefit").WithSummary("Record an employee benefit enrollment")
            .WithDescription("Records a benefit enrollment without calculating contributions or payroll deductions.")
            .Produces<EmployeeBenefitDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.BenefitsManage);
        group.MapPut("/employee-benefits/{id:guid}", UpdateBenefit)
            .WithName("UpdateHcmBenefit").WithSummary("Update an employee benefit enrollment")
            .Produces<EmployeeBenefitDto>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.BenefitsManage);
        group.MapGet("/training-records", ListTrainingRecords)
            .WithName("ListHcmTrainingRecords").WithSummary("List completed employee training")
            .WithDescription("Returns training records visible to the active tenant and company.")
            .Produces<IReadOnlyList<TrainingRecordDto>>().RequirePermission(HcmPermissions.TrainingRead);
        group.MapGet("/training-records/{id:guid}", GetTrainingRecord)
            .WithName("GetHcmTrainingRecord").WithSummary("Get a completed training record")
            .Produces<TrainingRecordDto>().Produces(StatusCodes.Status404NotFound)
            .RequirePermission(HcmPermissions.TrainingRead);
        group.MapPost("/training-records", CreateTrainingRecord)
            .WithName("CreateHcmTrainingRecord").WithSummary("Record completed employee training")
            .WithDescription("Stores training completion and optional certificate and expiry metadata.")
            .Produces<TrainingRecordDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.TrainingManage);
        group.MapPut("/training-records/{id:guid}", UpdateTrainingRecord)
            .WithName("UpdateHcmTrainingRecord").WithSummary("Update a completed training record")
            .Produces<TrainingRecordDto>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.TrainingManage);
        group.MapGet("/performance-reviews", ListPerformanceReviews)
            .WithName("ListHcmPerformanceReviews").WithSummary("List confidential performance reviews")
            .WithDescription("Returns narrative reviews visible to the active tenant and company.")
            .Produces<IReadOnlyList<PerformanceReviewDto>>()
            .RequirePermission(HcmPermissions.PerformanceReviewsRead);
        group.MapGet("/performance-reviews/{id:guid}", GetPerformanceReview)
            .WithName("GetHcmPerformanceReview").WithSummary("Get a confidential performance review")
            .Produces<PerformanceReviewDto>().Produces(StatusCodes.Status404NotFound)
            .RequirePermission(HcmPermissions.PerformanceReviewsRead);
        group.MapPost("/performance-reviews", CreatePerformanceReview)
            .WithName("CreateHcmPerformanceReview").WithSummary("Record a narrative performance review")
            .WithDescription("Stores review notes without imposing a rating scale or workflow.")
            .Produces<PerformanceReviewDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.PerformanceReviewsManage);
        group.MapPut("/performance-reviews/{id:guid}", UpdatePerformanceReview)
            .WithName("UpdateHcmPerformanceReview").WithSummary("Update a narrative performance review")
            .Produces<PerformanceReviewDto>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.PerformanceReviewsManage);
        group.MapGet("/payroll-runs", ListPayrollRuns)
            .WithName("ListHcmPayrollRuns").WithSummary("List payroll runs")
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapGet("/payroll-runs/{id:guid}", GetPayrollRun)
            .WithName("GetHcmPayrollRun").WithSummary("Get a payroll run and its calculation breakdown")
            .Produces<PayrollRunDto>().Produces(StatusCodes.Status404NotFound)
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapGet("/payroll-periods", ListPayrollPeriods)
            .WithName("ListHcmPayrollPeriods").WithSummary("List payroll periods")
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapGet("/payroll-periods/{id:guid}", GetPayrollPeriod)
            .WithName("GetHcmPayrollPeriod").WithSummary("Get a payroll period")
            .Produces<PayrollPeriodDto>().Produces(StatusCodes.Status404NotFound)
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapPost("/payroll-periods", CreatePayrollPeriod)
            .WithName("CreateHcmPayrollPeriod").WithSummary("Create an open payroll period")
            .Produces<PayrollPeriodDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict).RequirePermission(HcmPermissions.PayrollManage);
        group.MapPost("/payroll-periods/{id:guid}/runs", CreatePayrollRun)
            .WithName("CreateHcmPayrollRun").WithSummary("Calculate and persist a payroll run from saved inputs")
            .Produces<PayrollRunDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict).RequirePermission(HcmPermissions.PayrollManage);
        group.MapPost("/payroll-periods/{id:guid}/close", ClosePayrollPeriod)
            .WithName("CloseHcmPayrollPeriod").WithSummary("Close an approved or paid payroll period")
            .Produces<PayrollPeriodDto>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict).RequirePermission(HcmPermissions.PayrollApprove);
        group.MapGet("/payroll/rules", ListPayrollRules)
            .WithName("ListHcmPayrollRules").WithSummary("List immutable versioned payroll rules")
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapPost("/payroll/rules", AddPayrollRule)
            .WithName("AddHcmPayrollRule").WithSummary("Add a versioned payroll rule configuration")
            .Produces<PayrollRuleConfigurationDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict).RequirePermission(HcmPermissions.PayrollManage);
        group.MapGet("/payroll/compensation", ListCompensation)
            .WithName("ListHcmPayrollCompensation").WithSummary("List effective-dated employee compensation")
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapPost("/payroll/compensation", AddCompensation)
            .WithName("AddHcmPayrollCompensation").WithSummary("Add an effective-dated employee compensation version")
            .Produces<EmployeeCompensationDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .Produces(StatusCodes.Status409Conflict).RequirePermission(HcmPermissions.PayrollManage);
        group.MapGet("/payroll/employees", ListPayrollEmployees)
            .WithName("ListHcmPayrollEmployees").WithSummary("List employees available for payroll configuration")
            .RequirePermission(HcmPermissions.PayrollManage);
        group.MapPost("/payroll-runs/{id:guid}/transition", TransitionPayrollRun)
            .WithName("TransitionHcmPayrollRun").WithSummary("Mark an approved payroll run paid")
            .Produces<PayrollRunDto>().ProducesValidationProblem().Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict).RequirePermission(HcmPermissions.PayrollApprove);
        group.MapPost("/payroll-runs/{id:guid}/submit", SubmitPayrollRun)
            .WithName("SubmitHcmPayrollRun").WithSummary("Validate and submit a payroll run for approval")
            .RequirePermission(HcmPermissions.PayrollManage);
        group.MapPost("/payroll-runs/{id:guid}/approve", (Guid id, PayrollReviewRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.ReviewPayrollRunAsync(id, true, request, token)))
            .WithName("ApproveHcmPayrollRun").WithSummary("Approve a submitted payroll run")
            .RequirePermission(HcmPermissions.PayrollApprove);
        group.MapPost("/payroll-runs/{id:guid}/reject", (Guid id, PayrollReviewRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.ReviewPayrollRunAsync(id, false, request, token)))
            .WithName("RejectHcmPayrollRun").WithSummary("Reject a submitted payroll run with a reason")
            .RequirePermission(HcmPermissions.PayrollApprove);
        group.MapGet("/payroll-runs/{id:guid}/approval-history", (Guid id, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.GetPayrollApprovalHistoryAsync(id, token)))
            .WithName("GetHcmPayrollApprovalHistory").RequirePermission(HcmPermissions.PayrollRead);
        group.MapPost("/payroll-runs/{id:guid}/post", (Guid id, PayrollPostingRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.PostPayrollRunAsync(id, request, token)))
            .WithName("PostHcmPayrollRun").WithSummary("Create and post the approved payroll journal through Accounting")
            .RequirePermission(HcmPermissions.PayrollApprove);
        group.MapPost("/payroll-runs/{id:guid}/reverse", (Guid id, PayrollReversalRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.ReversePayrollRunAsync(id, request, token)))
            .WithName("ReverseHcmPayrollRun").WithSummary("Reverse posted payroll through an Accounting compensating journal")
            .Produces<PayrollReversalDto>().ProducesValidationProblem().Produces(StatusCodes.Status409Conflict)
            .RequirePermission(HcmPermissions.PayrollApprove);
        group.MapGet("/payroll/account-mappings", ([FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.ListPayrollAccountMappingsAsync(token)))
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapGet("/payroll-runs/{id:guid}/posting-status", (Guid id, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.GetPayrollPostingStatusAsync(id, token)))
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapGet("/payroll-runs/{id:guid}/reconciliation", (Guid id, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.ReconcilePayrollRunAsync(id, token)))
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapPost("/payroll/account-mappings", (PayrollAccountMappingRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.SavePayrollAccountMappingAsync(request, token)))
            .RequirePermission(HcmPermissions.PayrollManage);
        group.MapGet("/payroll-adjustments", (Guid? payrollRunId, Guid? employeeId, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.ListPayrollAdjustmentsAsync(payrollRunId, employeeId, token)))
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapGet("/payroll-adjustments/{id:guid}", async (Guid id, [FromServices] IHcmService service, CancellationToken token) =>
                await service.GetPayrollAdjustmentAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .RequirePermission(HcmPermissions.PayrollRead);
        group.MapPost("/payroll-adjustments", (PayrollAdjustmentRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.CreatePayrollAdjustmentAsync(request, token), "/api/v1/hcm/payroll-adjustments"))
            .Produces<PayrollAdjustmentDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .RequirePermission(HcmPermissions.PayrollAdjust);
        group.MapPost("/payroll-adjustments/recalculate", (PayrollRecalculatedAdjustmentRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.CreateRecalculatedPayrollAdjustmentAsync(request, token), "/api/v1/hcm/payroll-adjustments"))
            .Produces<PayrollAdjustmentDto>(StatusCodes.Status201Created).ProducesValidationProblem()
            .RequirePermission(HcmPermissions.PayrollAdjust);
        group.MapPost("/payroll-adjustments/{id:guid}/submit", (Guid id, PayrollAdjustmentReviewRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.SubmitPayrollAdjustmentAsync(id, request, token)))
            .RequirePermission(HcmPermissions.PayrollAdjust);
        group.MapPost("/payroll-adjustments/{id:guid}/approve", (Guid id, PayrollAdjustmentReviewRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.ReviewPayrollAdjustmentAsync(id, true, request, token)))
            .RequirePermission(HcmPermissions.PayrollApprove);
        group.MapPost("/payroll-adjustments/{id:guid}/reject", (Guid id, PayrollAdjustmentReviewRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.ReviewPayrollAdjustmentAsync(id, false, request, token)))
            .RequirePermission(HcmPermissions.PayrollApprove);
        group.MapPost("/payroll-adjustments/{id:guid}/post", (Guid id, PayrollAdjustmentPostingRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.PostPayrollAdjustmentAsync(id, request, token)))
            .RequirePermission(HcmPermissions.PayrollApprove);
        group.MapPost("/payroll-adjustments/{id:guid}/reverse", (Guid id, PayrollAdjustmentReversalRequest request, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.ReversePayrollAdjustmentAsync(id, request, token)))
            .RequirePermission(HcmPermissions.PayrollApprove);
        group.MapGet("/payroll-adjustments/{id:guid}/history", (Guid id, [FromServices] IHcmService service, CancellationToken token) =>
                Execute(() => service.GetPayrollAdjustmentHistoryAsync(id, token)))
            .RequirePermission(HcmPermissions.PayrollRead);
    }

    private static Task<IResult> ListEmployees(string? search, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.ListEmployeesAsync(search, token));

    private static IResult EmployeeTemplate() => Results.Text("employeeNumber,fullName,email,hireDate\r\nEMP-001,Alex Morgan,alex@example.com,2026-01-15\r\n", "text/csv; charset=utf-8", Encoding.UTF8, 200);

    private static async Task<IResult> ExportEmployees(string? search, [FromServices] IHcmService service, CancellationToken token)
    {
        var employees = await service.ListEmployeesAsync(search, token);
        var csv = new StringBuilder("employeeNumber,fullName,email,hireDate,status\r\n");
        foreach (var employee in employees) csv.AppendLine(string.Join(',', new[] { employee.EmployeeNumber, employee.FullName, employee.Email ?? string.Empty, employee.HireDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), employee.Status.ToString() }.Select(CsvField)));
        return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", "employees.csv");
    }

    private static async Task<IResult> ImportEmployees(IFormFile file, [FromServices] IHcmService service, CancellationToken token)
    {
        if (file.Length == 0 || file.Length > 5_000_000) return Results.BadRequest(new EmployeeImportResult(0, ["CSV must contain data and be no larger than 5 MB."]));
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, true);
        var header = await reader.ReadLineAsync(token);
        if (header is null) return Results.BadRequest(new EmployeeImportResult(0, ["CSV is empty."]));
        var columns = ParseCsv(header).Select((value, index) => (Name: value.Trim(), Index: index)).ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
        var required = new[] { "employeeNumber", "fullName", "email", "hireDate" };
        if (required.Any(x => !columns.ContainsKey(x))) return Results.BadRequest(new EmployeeImportResult(0, ["Header must include employeeNumber, fullName, email, and hireDate."]));
        var errors = new List<string>(); var imported = 0; var row = 1;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            row++; if (row > 10_001) { errors.Add("Import cannot exceed 10,000 data rows."); break; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var fields = ParseCsv(line); string? Get(string key) => columns[key] < fields.Count ? fields[columns[key]] : null;
                if (!DateOnly.TryParseExact(Get("hireDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var hireDate)) throw new FormatException("hireDate must use YYYY-MM-DD.");
                await service.CreateEmployeeAsync(new(Get("employeeNumber"), Get("fullName"), Get("email"), hireDate), token); imported++;
            }
            catch (Exception exception) when (exception is HcmValidationException or HcmConflictException or FormatException) { errors.Add($"Line {row}: {exception.Message}"); }
        }
        return Results.Ok(new EmployeeImportResult(imported, errors));
    }

    private static string CsvField(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
    private static List<string> ParseCsv(string line)
    {
        var fields = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++) { var c = line[i]; if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else if (c == '"') quoted = !quoted; else if (c == ',' && !quoted) { fields.Add(value.ToString()); value.Clear(); } else value.Append(c); }
        if (quoted) throw new FormatException("CSV row contains an unclosed quote."); fields.Add(value.ToString()); return fields;
    }
    private sealed record EmployeeImportResult(int ImportedCount, IReadOnlyList<string> Errors);

    private static Task<IResult> ListPayrollRuns([FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.ListPayrollRunsAsync(token));

    private static async Task<IResult> GetPayrollRun(Guid id, [FromServices] IHcmService service, CancellationToken token) =>
        await service.GetPayrollRunAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound();

    private static Task<IResult> ListPayrollPeriods([FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.ListPayrollPeriodsAsync(token));

    private static async Task<IResult> GetPayrollPeriod(Guid id, [FromServices] IHcmService service, CancellationToken token) =>
        await service.GetPayrollPeriodAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound();

    private static Task<IResult> CreatePayrollPeriod(PayrollPeriodRequest request,
        [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.CreatePayrollPeriodAsync(request, token), "/api/v1/hcm/payroll-periods");

    private static Task<IResult> CreatePayrollRun(Guid id, CreatePayrollRunRequest request,
        [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.CreatePayrollRunAsync(id, request, token),
            $"/api/v1/hcm/payroll-runs");

    private static Task<IResult> ClosePayrollPeriod(Guid id,
        [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.ClosePayrollPeriodAsync(id, token));

    private static Task<IResult> FinalizeAttendance(Guid id, FinalizeAttendanceRequest request,
        [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.FinalizeAttendanceAsync(id, request, token));

    private static Task<IResult> RecordAttendanceDisposition(RecordAttendanceDispositionRequest request,
        [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.RecordAttendanceDispositionAsync(request, token), "/api/v1/hcm/attendance");

    private static Task<IResult> ListPayrollRules([FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.ListPayrollRulesAsync(token));

    private static Task<IResult> AddPayrollRule(PayrollRuleConfigurationRequest request,
        [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.AddPayrollRuleAsync(request, token), "/api/v1/hcm/payroll/rules");

    private static Task<IResult> ListCompensation(Guid? employeeId, [FromServices] IHcmService service,
        CancellationToken token) => Execute(() => service.ListCompensationAsync(employeeId, token));

    private static Task<IResult> ListPayrollEmployees(string? search, [FromServices] IHcmService service,
        CancellationToken token) => Execute(() => service.ListEmployeesAsync(search, token));

    private static Task<IResult> AddCompensation(EmployeeCompensationRequest request,
        [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.AddCompensationAsync(request, token), "/api/v1/hcm/payroll/compensation");

    private static Task<IResult> TransitionPayrollRun(Guid id, PayrollRunTransition request,
        [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.TransitionPayrollRunAsync(id, request.Status, request.ConcurrencyStamp, token));

    private static Task<IResult> SubmitPayrollRun(Guid id, PayrollReviewRequest request,
        [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.SubmitPayrollRunAsync(id, request, token));

    private static async Task<IResult> GetEmployee(Guid id, [FromServices] IHcmService service, CancellationToken token)
    {
        var employee = await service.GetEmployeeAsync(id, token);
        return employee is null ? Results.NotFound() : Results.Ok(employee);
    }

    private static Task<IResult> CreateEmployee(
        CreateEmployeeRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.CreateEmployeeAsync(request, token), $"/api/v1/hcm/employees");

    private static Task<IResult> UpdateEmployee(
        Guid id, UpdateEmployeeRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.UpdateEmployeeAsync(id, request, token));

    private static Task<IResult> ListLeaveRequests(
        Guid? employeeId, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.ListLeaveRequestsAsync(employeeId, token));

    private static async Task<IResult> GetLeaveRequest(Guid id, [FromServices] IHcmService service, CancellationToken token)
    {
        var request = await service.GetLeaveRequestAsync(id, token);
        return request is null ? Results.NotFound() : Results.Ok(request);
    }

    private static Task<IResult> SubmitLeaveRequest(
        SubmitLeaveRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.SubmitLeaveRequestAsync(request, token), "/api/v1/hcm/leave-requests");

    private static Task<IResult> DecideLeaveRequest(
        Guid id, LeaveRequestDecision request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.DecideLeaveRequestAsync(id, request.Decision, token));

    private static Task<IResult> ListAttendance(
        Guid? employeeId, DateOnly? workDate, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.ListAttendanceAsync(employeeId, workDate, token));

    private static async Task<IResult> GetAttendance(Guid id, [FromServices] IHcmService service, CancellationToken token)
    {
        var attendance = await service.GetAttendanceAsync(id, token);
        return attendance is null ? Results.NotFound() : Results.Ok(attendance);
    }

    private static Task<IResult> CheckIn(
        CheckInRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.CheckInAsync(request, token), "/api/v1/hcm/attendance");

    private static Task<IResult> CheckOut(
        Guid id, CheckOutRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.CheckOutAsync(id, request.CheckedOutAtUtc, token));

    private static Task<IResult> ListBenefits(
        Guid? employeeId, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.ListBenefitsAsync(employeeId, token));

    private static async Task<IResult> GetBenefit(Guid id, [FromServices] IHcmService service, CancellationToken token)
    {
        var benefit = await service.GetBenefitAsync(id, token);
        return benefit is null ? Results.NotFound() : Results.Ok(benefit);
    }

    private static Task<IResult> CreateBenefit(
        CreateEmployeeBenefitRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.CreateBenefitAsync(request, token), "/api/v1/hcm/employee-benefits");

    private static Task<IResult> UpdateBenefit(
        Guid id, UpdateEmployeeBenefitRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.UpdateBenefitAsync(id, request, token));

    private static Task<IResult> ListTrainingRecords(
        Guid? employeeId, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.ListTrainingRecordsAsync(employeeId, token));

    private static async Task<IResult> GetTrainingRecord(Guid id, [FromServices] IHcmService service, CancellationToken token)
    {
        var record = await service.GetTrainingRecordAsync(id, token);
        return record is null ? Results.NotFound() : Results.Ok(record);
    }

    private static Task<IResult> CreateTrainingRecord(
        CreateTrainingRecordRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.CreateTrainingRecordAsync(request, token), "/api/v1/hcm/training-records");

    private static Task<IResult> UpdateTrainingRecord(
        Guid id, UpdateTrainingRecordRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.UpdateTrainingRecordAsync(id, request, token));

    private static Task<IResult> ListPerformanceReviews(
        Guid? employeeId, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.ListPerformanceReviewsAsync(employeeId, token));

    private static async Task<IResult> GetPerformanceReview(Guid id, [FromServices] IHcmService service, CancellationToken token)
    {
        var review = await service.GetPerformanceReviewAsync(id, token);
        return review is null ? Results.NotFound() : Results.Ok(review);
    }

    private static Task<IResult> CreatePerformanceReview(
        CreatePerformanceReviewRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.CreatePerformanceReviewAsync(request, token), "/api/v1/hcm/performance-reviews");

    private static Task<IResult> UpdatePerformanceReview(
        Guid id, UpdatePerformanceReviewRequest request, [FromServices] IHcmService service, CancellationToken token) =>
        Execute(() => service.UpdatePerformanceReviewAsync(id, request, token));

    private static async Task<IResult> Execute<T>(Func<Task<T>> action, string? location = null)
    {
        try
        {
            var value = await action();
            return location is null ? Results.Ok(value) : Results.Created($"{location}/{GetId(value)}", value);
        }
        catch (HcmValidationException exception) { return Results.ValidationProblem(exception.Errors); }
        catch (HcmConflictException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (HcmConcurrencyException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static Guid GetId<T>(T value) => value switch
    {
        EmployeeDto employee => employee.Id,
        LeaveRequestDto leave => leave.Id,
        AttendanceRecordDto attendance => attendance.Id,
        EmployeeBenefitDto benefit => benefit.Id,
        TrainingRecordDto training => training.Id,
        PerformanceReviewDto review => review.Id,
        PayrollRunDto payrollRun => payrollRun.Id,
        _ => throw new InvalidOperationException("Unsupported HCM response type.")
    };
}

/// <summary>Decision applied by an authorized leave approver.</summary>
public sealed record LeaveRequestDecision(LeaveRequestStatus Decision);

/// <summary>UTC check-out timestamp for an attendance record.</summary>
public sealed record CheckOutRequest(DateTimeOffset CheckedOutAtUtc);
public sealed record PayrollRunTransition(PayrollRunStatus Status, Guid ConcurrencyStamp);
