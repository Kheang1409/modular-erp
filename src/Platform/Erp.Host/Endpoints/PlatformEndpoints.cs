using System.Security.Claims;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Security;
using Erp.Abstractions.Currencies;
using Erp.Abstractions.Files;
using System.Text.Json;
using Erp.Abstractions.Operations;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace Erp.Host.Endpoints;

public static class PlatformEndpoints
{
    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/platform/context", GetOrganizationContextAsync)
            .WithName("GetOrganizationContext")
            .WithTags("Platform")
            .RequirePermission(PlatformPermissions.OrganizationRead);
        MapOrganizationEndpoints(endpoints);

        MapRoleEndpoints(endpoints);
        endpoints.MapUserAdministrationEndpoints();
        MapCurrencyEndpoints(endpoints);
        MapAttachmentEndpoints(endpoints);
        MapModuleEndpoints(endpoints);
        MapQueueOperationsEndpoints(endpoints);
        endpoints.MapGet("/api/v1/platform/audit", GetAuditAsync)
            .WithTags("Platform audit")
            .RequirePermission(PlatformPermissions.AuditRead);
        return endpoints;
    }

    private static void MapOrganizationEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/api/v1/platform/organization", async Task<IResult> (
            UpdateOrganizationRequest request, IOrganizationAdministrationService service, CancellationToken token) =>
        {
            try { return Results.Ok(await service.UpdateOrganizationAsync(request, token)); }
            catch (ArgumentException exception) { return BadRequest(exception); }
            catch (InvalidOperationException exception) { return Conflict(exception); }
        }).WithTags("Platform organization").RequirePermission(PlatformPermissions.OrganizationManage);
        endpoints.MapGet("/api/v1/platform/companies", async (
                HttpContext context, IOrganizationAdministrationService service, CancellationToken token) =>
            {
                var userId = CurrentUserId(context);
                var canListAll = context.User.HasClaim("permission", PlatformPermissions.OrganizationManage)
                    || context.User.HasClaim("permission", PlatformPermissions.SecurityManage);
                return userId is null ? Results.Unauthorized() : Results.Ok(await service.ListCompaniesAsync(userId.Value, token, canListAll));
            })
            .WithTags("Platform organization")
            .RequirePermission(PlatformPermissions.OrganizationRead);
        endpoints.MapPost("/api/v1/platform/companies", async (
                CreateCompanyRequest request, HttpContext context, IOrganizationAdministrationService service, CancellationToken token) =>
            {
                var userId = CurrentUserId(context);
                if (userId is null) return Results.Unauthorized();
                try
                {
                    var company = await service.CreateCompanyAsync(userId.Value, request, token);
                    return Results.Created($"/api/v1/platform/companies/{company.Id}", company);
                }
                catch (ArgumentException exception) { return BadRequest(exception); }
                catch (InvalidOperationException exception) { return Conflict(exception); }
            })
            .WithTags("Platform organization")
            .RequirePermission(PlatformPermissions.OrganizationManage);
        endpoints.MapPut("/api/v1/platform/companies/{companyId:guid}", async (
                Guid companyId, UpdateCompanyRequest request, IOrganizationAdministrationService service, CancellationToken token) =>
            {
                try
                {
                    var company = await service.UpdateCompanyAsync(companyId, request, token);
                    return company is null ? Results.NotFound() : Results.Ok(company);
                }
                catch (ArgumentException exception) { return BadRequest(exception); }
                catch (InvalidOperationException exception) { return Conflict(exception); }
            })
            .WithTags("Platform organization")
            .RequirePermission(PlatformPermissions.OrganizationManage);
    }

    private static Guid? CurrentUserId(HttpContext context) =>
        Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;

    public static void MapQueueOperationsEndpoints(IEndpointRouteBuilder endpoints)
    {
        var operations = endpoints.MapGroup("/api/v1/platform/operations")
            .WithTags("Platform operations")
            .RequirePermission(PlatformPermissions.OperationsManage);
        operations.MapGet("/dead-letters", async (int? limit, IQueueOperationsService service, CancellationToken token) =>
        {
            try { return Results.Ok(await service.GetAsync(limit ?? 50, token)); }
            catch (ArgumentOutOfRangeException exception) { return BadRequest(exception); }
        });
        operations.MapPost("/outbox/{id:guid}/replay", async (Guid id, IQueueOperationsService service, CancellationToken token) =>
            await service.ReplayOutboxAsync(id, token) ? Results.NoContent() : Results.NotFound());
        operations.MapPost("/jobs/{id:guid}/replay", async (Guid id, IQueueOperationsService service, CancellationToken token) =>
            await service.ReplayJobAsync(id, token) ? Results.NoContent() : Results.NotFound());
        operations.MapGet("/maintenance/preview", async Task<IResult> (int? retentionDays, IDataMaintenanceService service, CancellationToken token) =>
        {
            try { return Results.Ok(await service.PreviewAsync(retentionDays ?? 90, token)); }
            catch (ArgumentException exception) { return BadRequest(exception); }
            catch (InvalidOperationException exception) { return Conflict(exception); }
        });
        operations.MapPost("/maintenance/execute", async Task<IResult> (ExecuteDataMaintenanceRequest request, IDataMaintenanceService service, CancellationToken token) =>
        {
            try { return Results.Ok(await service.ExecuteAsync(request, token)); }
            catch (ArgumentException exception) { return BadRequest(exception); }
            catch (InvalidOperationException exception) { return Conflict(exception); }
        });
    }

    public static void MapModuleEndpoints(IEndpointRouteBuilder endpoints)
    {
        var modules = endpoints.MapGroup("/api/v1/platform/modules")
            .WithTags("Platform modules")
            .RequirePermission(PlatformPermissions.ModuleManage);
        modules.MapGet("/", (IModulePackageManager manager) => manager.List());
        modules.MapPost("/validate", (HttpRequest request, IModulePackageManager manager, CancellationToken token) =>
            ProcessModuleArchiveAsync(request, manager, null, token));
        modules.MapPost("/install", (HttpRequest request, IModulePackageManager manager, IAuditLogWriter audit, CancellationToken token) =>
            ProcessModuleArchiveAsync(request, manager, audit, token, update: false));
        modules.MapPost("/update", (HttpRequest request, IModulePackageManager manager, IAuditLogWriter audit, CancellationToken token) =>
            ProcessModuleArchiveAsync(request, manager, audit, token, update: true));
        modules.MapPost("/{moduleId}/enable", (string moduleId, IModulePackageManager manager, IAuditLogWriter audit, CancellationToken token) =>
            ChangeModuleStateAsync(moduleId, true, manager, audit, token));
        modules.MapPost("/{moduleId}/disable", (string moduleId, IModulePackageManager manager, IAuditLogWriter audit, CancellationToken token) =>
            ChangeModuleStateAsync(moduleId, false, manager, audit, token));
    }

    private static async Task<IResult> ProcessModuleArchiveAsync(
        HttpRequest request,
        IModulePackageManager manager,
        IAuditLogWriter? audit,
        CancellationToken cancellationToken,
        bool update = false)
    {
        const long maximumPackageSize = 50 * 1024 * 1024;
        if (request.ContentLength is null or <= 0 or > maximumPackageSize)
            return Results.BadRequest(new ProblemDetails { Title = "A module package up to 50 MB is required.", Status = 400 });
        var temporary = Path.Combine(Path.GetTempPath(), $"erp-module-{Guid.NewGuid():N}.erpmod");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await request.Body.CopyToAsync(stream, cancellationToken);
            var result = audit is null ? manager.Validate(temporary) : manager.Install(temporary, update);
            if (audit is not null)
            {
                audit.Append(new(update ? "platform.module.updated" : "platform.module.installed", "Module", result.Id,
                    JsonSerializer.Serialize(new { result.Version, result.RestartRequired }), null, null));
                await audit.FlushAsync(cancellationToken);
            }
            return Results.Ok(result);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or FileNotFoundException)
        {
            return Results.BadRequest(new ProblemDetails { Title = exception.Message, Status = 400 });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<IResult> ChangeModuleStateAsync(
        string moduleId,
        bool enabled,
        IModulePackageManager manager,
        IAuditLogWriter audit,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = manager.SetEnabled(moduleId, enabled);
            audit.Append(new(enabled ? "platform.module.enabled" : "platform.module.disabled", "Module", result.Id,
                JsonSerializer.Serialize(new { result.Version, result.RestartRequired }), null, null));
            await audit.FlushAsync(cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return Results.Conflict(new ProblemDetails { Title = exception.Message, Status = 409 });
        }
    }

    private static void MapAttachmentEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/platform/attachments", ListAttachmentsAsync)
            .WithTags("Platform attachments")
            .RequirePermission(PlatformPermissions.AttachmentRead);
        endpoints.MapGet("/api/v1/platform/attachments/{attachmentId:guid}/content", DownloadAttachmentAsync)
            .WithTags("Platform attachments")
            .RequirePermission(PlatformPermissions.AttachmentRead);
        endpoints.MapPost("/api/v1/platform/attachments", UploadAttachmentAsync)
            .WithTags("Platform attachments")
            .RequirePermission(PlatformPermissions.AttachmentManage);
    }

    private static async Task<IResult> UploadAttachmentAsync(
        string entityType,
        string entityId,
        HttpRequest httpRequest,
        IAttachmentService service,
        CancellationToken cancellationToken)
    {
        if (httpRequest.ContentLength is null)
            return Results.BadRequest(new ProblemDetails { Title = "Content-Length is required.", Status = 400 });
        var fileName = httpRequest.Headers["X-File-Name"].ToString();
        try
        {
            var attachment = await service.UploadAsync(new(
                fileName, httpRequest.ContentType ?? "application/octet-stream", httpRequest.ContentLength.Value,
                entityType, entityId), httpRequest.Body, cancellationToken);
            return Results.Created($"/api/v1/platform/attachments/{attachment.Id}/content", attachment);
        }
        catch (ArgumentException exception) { return BadRequest(exception); }
        catch (InvalidDataException exception) { return BadRequest(exception); }
        catch (InvalidOperationException exception) { return Conflict(exception); }
    }

    private static async Task<IResult> ListAttachmentsAsync(
        string entityType,
        string entityId,
        IAttachmentService service,
        CancellationToken cancellationToken)
    {
        try { return Results.Ok(await service.ListAsync(entityType, entityId, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(exception); }
    }

    private static async Task<IResult> DownloadAttachmentAsync(
        Guid attachmentId,
        IAttachmentService service,
        IFileStorage storage,
        CancellationToken cancellationToken)
    {
        var download = await service.GetDownloadAsync(attachmentId, cancellationToken);
        return download is null
            ? Results.NotFound()
            : Results.Stream(
                stream => storage.ReadAsync(download.StorageKey, stream, cancellationToken),
                download.Attachment.ContentType,
                download.Attachment.FileName);
    }

    private static void MapCurrencyEndpoints(IEndpointRouteBuilder endpoints)
    {
        var currencies = endpoints.MapGroup("/api/v1/platform/currencies")
            .WithTags("Platform currencies")
            .RequirePermission(PlatformPermissions.CurrencyManage);
        currencies.MapGet("/", (ICurrencyConfigurationService service, CancellationToken cancellationToken) =>
            service.GetAsync(cancellationToken));
        currencies.MapPut("/{code}", ConfigureCurrencyAsync);
        currencies.MapPut("/base", SetBaseCurrencyAsync);
        currencies.MapPost("/exchange-rates", AddExchangeRateAsync);
    }

    private static async Task<IResult> ConfigureCurrencyAsync(
        string code,
        ConfigureCurrencyRequest request,
        ICurrencyConfigurationService service,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(code, request.Code, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new ProblemDetails { Title = "Route and request currency codes must match.", Status = 400 });
        try { return Results.Ok(await service.ConfigureAsync(request, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(exception); }
        catch (InvalidOperationException exception) { return Conflict(exception); }
    }

    private static async Task<IResult> SetBaseCurrencyAsync(
        SetBaseCurrencyRequest request,
        ICurrencyConfigurationService service,
        CancellationToken cancellationToken)
    {
        try { return Results.Ok(await service.SetBaseCurrencyAsync(request, cancellationToken)); }
        catch (ArgumentException exception) { return BadRequest(exception); }
        catch (InvalidOperationException exception) { return Conflict(exception); }
    }

    private static async Task<IResult> AddExchangeRateAsync(
        AddExchangeRateRequest request,
        ICurrencyConfigurationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var rate = await service.AddExchangeRateAsync(request, cancellationToken);
            return Results.Created($"/api/v1/platform/currencies/exchange-rates/{rate.Id}", rate);
        }
        catch (ArgumentException exception) { return BadRequest(exception); }
        catch (InvalidOperationException exception) { return Conflict(exception); }
    }

    private static void MapRoleEndpoints(IEndpointRouteBuilder endpoints)
    {
        var roles = endpoints.MapGroup("/api/v1/platform/roles")
            .WithTags("Platform roles")
            .RequirePermission(PlatformPermissions.SecurityManage);
        roles.MapGet("/", (IRoleAdministrationService service, CancellationToken cancellationToken) =>
            service.ListAsync(cancellationToken));
        roles.MapPost("/", CreateRoleAsync);
        roles.MapPut("/{roleId:guid}/permissions", SetRolePermissionsAsync);
        roles.MapGet("/users", (IRoleAdministrationService service, CancellationToken cancellationToken) =>
            service.ListUsersAsync(cancellationToken));
        roles.MapPut("/users/{userId:guid}", SetUserRolesAsync);
        roles.MapPut("/users/{userId:guid}/companies", SetUserCompaniesAsync);
        roles.MapPut("/users/{userId:guid}/access", async Task<IResult> (Guid userId, SetUserAccessRequest request, IRoleAdministrationService service, CancellationToken token) =>
        {
            try
            {
                var user = await service.SetUserAccessAsync(userId, request, token);
                return user is null ? Results.NotFound() : Results.Ok(user);
            }
            catch (ArgumentException exception) { return BadRequest(exception); }
            catch (InvalidOperationException exception) { return Conflict(exception); }
        });
    }

    private static async Task<IResult> GetOrganizationContextAsync(
        HttpContext httpContext,
        IOrganizationContextReader reader,
        CancellationToken cancellationToken)
    {
        var identifier = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(identifier, out var userId)) return Results.Unauthorized();
        var organization = await reader.GetAsync(new UserId(userId), cancellationToken);
        return organization is null ? Results.NotFound() : Results.Ok(organization);
    }

    private static async Task<IResult> CreateRoleAsync(
        CreateRoleRequest request,
        IRoleAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var role = await service.CreateAsync(request, cancellationToken);
            return Results.Created($"/api/v1/platform/roles/{role.Id}", role);
        }
        catch (ArgumentException exception) { return BadRequest(exception); }
        catch (InvalidOperationException exception) { return Conflict(exception); }
    }

    private static async Task<IResult> SetRolePermissionsAsync(
        Guid roleId,
        SetRolePermissionsRequest request,
        IRoleAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var role = await service.SetPermissionsAsync(roleId, request, cancellationToken);
            return role is null ? Results.NotFound() : Results.Ok(role);
        }
        catch (ArgumentException exception) { return BadRequest(exception); }
        catch (InvalidOperationException exception) { return Conflict(exception); }
    }

    private static async Task<IResult> SetUserRolesAsync(
        Guid userId,
        SetUserRolesRequest request,
        IRoleAdministrationService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await service.SetUserRolesAsync(userId, request, cancellationToken);
            return user is null ? Results.NotFound() : Results.Ok(user);
        }
        catch (ArgumentException exception) { return BadRequest(exception); }
        catch (InvalidOperationException exception) { return Conflict(exception); }
    }

    private static async Task<IResult> SetUserCompaniesAsync(Guid userId, SetUserCompaniesRequest request, IRoleAdministrationService service, CancellationToken cancellationToken)
    {
        try
        {
            var user = await service.SetUserCompaniesAsync(userId, request, cancellationToken);
            return user is null ? Results.NotFound() : Results.Ok(user);
        }
        catch (ArgumentException exception) { return BadRequest(exception); }
        catch (InvalidOperationException exception) { return Conflict(exception); }
    }

    private static async Task<IResult> GetAuditAsync(
        int? offset,
        int? limit,
        IAuditLogReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await reader.ReadAsync(offset ?? 0, limit ?? 50, cancellationToken));
        }
        catch (ArgumentOutOfRangeException exception) { return BadRequest(exception); }
    }

    private static IResult BadRequest(Exception exception) =>
        Results.BadRequest(new ProblemDetails { Title = exception.Message, Status = StatusCodes.Status400BadRequest });

    private static IResult Conflict(Exception exception) =>
        Results.Conflict(new ProblemDetails { Title = exception.Message, Status = StatusCodes.Status409Conflict });
}
