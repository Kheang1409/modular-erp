using Erp.Abstractions.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Modules.Contacts;

internal static class ContactEndpoints
{
    public static void MapContactsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/contacts").WithTags("Contacts");
        group.MapGet("/", Search).WithName("SearchContacts").RequirePermission(ContactsPermissions.Read);
        group.MapGet("/{id:guid}", Get).WithName("GetContact").RequirePermission(ContactsPermissions.Read);
        group.MapPost("/", Create).WithName("CreateContact").RequirePermission(ContactsPermissions.Manage);
        group.MapPut("/{id:guid}", Update).WithName("UpdateContact").RequirePermission(ContactsPermissions.Manage);
        group.MapPost("/{id:guid}/archive", Archive).WithName("ArchiveContact").RequirePermission(ContactsPermissions.Archive);
        group.MapPost("/{id:guid}/reactivate", Reactivate).WithName("ReactivateContact").RequirePermission(ContactsPermissions.Archive);
        group.MapPost("/import", Import).Accepts<IFormFile>("multipart/form-data")
            .WithName("ImportContactsCsv").RequirePermission(ContactsPermissions.Import);
        group.MapGet("/export", Export).Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .WithName("ExportContactsCsv").RequirePermission(ContactsPermissions.Export);
    }

    private static async Task<IResult> Search(
        IContactService service, string? search, ContactKind? kind, bool includeArchived = false,
        string sort = "name", bool descending = false, int page = 1, int pageSize = 25,
        bool? isCustomer = null, bool? isSupplier = null,
        CancellationToken cancellationToken = default) =>
        await Execute(() => service.SearchAsync(new(search, kind, includeArchived, sort, descending, page, pageSize, isCustomer, isSupplier), cancellationToken));

    private static async Task<IResult> Get(Guid id, IContactService service, CancellationToken cancellationToken) =>
        await service.GetAsync(id, cancellationToken) is { } contact ? Results.Ok(contact) : Results.NotFound();

    private static async Task<IResult> Create(ContactRequest request, IContactService service, CancellationToken cancellationToken)
    {
        var result = await Execute(() => service.CreateAsync(request, cancellationToken), created: true);
        return result;
    }

    private static Task<IResult> Update(Guid id, ContactRequest request, IContactService service, CancellationToken cancellationToken) =>
        Execute(() => service.UpdateAsync(id, request, cancellationToken));

    private static Task<IResult> Archive(Guid id, ConcurrencyRequest request, IContactService service, CancellationToken cancellationToken) =>
        Execute(() => service.SetArchivedAsync(id, request.ConcurrencyStamp, true, cancellationToken));

    private static Task<IResult> Reactivate(Guid id, ConcurrencyRequest request, IContactService service, CancellationToken cancellationToken) =>
        Execute(() => service.SetArchivedAsync(id, request.ConcurrencyStamp, false, cancellationToken));

    private static async Task<IResult> Import(IFormFile file, IContactService service, CancellationToken cancellationToken)
    {
        if (file.Length == 0 || file.Length > 5_000_000) return Results.BadRequest(new { error = "CSV must contain data and be no larger than 5 MB." });
        await using var stream = file.OpenReadStream();
        return Results.Ok(await service.ImportCsvAsync(stream, cancellationToken));
    }

    private static async Task<IResult> Export(
        IContactService service, string? search, ContactKind? kind, bool includeArchived = false,
        string sort = "name", bool descending = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new ContactQuery(search, kind, includeArchived, sort, descending, 1, 100);
            return Results.Stream(stream => service.ExportCsvAsync(query, stream, cancellationToken),
                "text/csv; charset=utf-8", "contacts.csv");
        }
        catch (ContactValidationException exception) { return Results.ValidationProblem(exception.Errors); }
    }

    private static async Task<IResult> Execute<T>(Func<Task<T>> operation, bool created = false)
    {
        try
        {
            var value = await operation();
            return created && value is ContactDto contact
                ? Results.Created($"/api/v1/contacts/{contact.Id}", value)
                : Results.Ok(value);
        }
        catch (ContactValidationException exception) { return Results.ValidationProblem(exception.Errors); }
        catch (ContactConcurrencyException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }
}

public sealed record ConcurrencyRequest(string ConcurrencyStamp);
