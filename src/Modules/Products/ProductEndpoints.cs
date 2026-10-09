using Erp.Abstractions.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Modules.Products;

internal static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/products").WithTags("Products");
        group.MapGet("/options", (IProductService service, CancellationToken cancellationToken) =>
                service.GetOptionsAsync(cancellationToken))
            .WithName("GetProductOptions").RequirePermission(ProductsPermissions.Read);
        group.MapGet("/", Search).WithName("SearchProducts").RequirePermission(ProductsPermissions.Read);
        group.MapGet("/{id:guid}", Get).WithName("GetProduct").RequirePermission(ProductsPermissions.Read);
        group.MapPost("/", Create).WithName("CreateProduct").RequirePermission(ProductsPermissions.Manage);
        group.MapPut("/{id:guid}", Update).WithName("UpdateProduct").RequirePermission(ProductsPermissions.Manage);
        group.MapPost("/{id:guid}/archive", Archive).WithName("ArchiveProduct").RequirePermission(ProductsPermissions.Archive);
        group.MapPost("/{id:guid}/reactivate", Reactivate).WithName("ReactivateProduct").RequirePermission(ProductsPermissions.Archive);
        group.MapPost("/import", Import).Accepts<IFormFile>("multipart/form-data")
            .WithName("ImportProductsCsv").RequirePermission(ProductsPermissions.Import);
        group.MapGet("/export", Export).Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .WithName("ExportProductsCsv").RequirePermission(ProductsPermissions.Export);
    }

    private static Task<IResult> Search(IProductService service, string? search, ProductType? type,
        bool includeArchived = false, bool? tracksInventory = null, string sort = "name", bool descending = false,
        int page = 1, int pageSize = 25, CancellationToken cancellationToken = default) => Execute(() =>
            service.SearchAsync(new(search, type, includeArchived, tracksInventory, sort, descending, page, pageSize), cancellationToken));

    private static async Task<IResult> Get(Guid id, IProductService service, CancellationToken cancellationToken) =>
        await service.GetAsync(id, cancellationToken) is { } product ? Results.Ok(product) : Results.NotFound();

    private static Task<IResult> Create(ProductRequest request, IProductService service, CancellationToken cancellationToken) =>
        Execute(() => service.CreateAsync(request, cancellationToken), true);

    private static Task<IResult> Update(Guid id, ProductRequest request, IProductService service, CancellationToken cancellationToken) =>
        Execute(() => service.UpdateAsync(id, request, cancellationToken));

    private static Task<IResult> Archive(Guid id, ProductConcurrencyRequest request, IProductService service, CancellationToken cancellationToken) =>
        Execute(() => service.SetArchivedAsync(id, request.ConcurrencyStamp, true, cancellationToken));

    private static Task<IResult> Reactivate(Guid id, ProductConcurrencyRequest request, IProductService service, CancellationToken cancellationToken) =>
        Execute(() => service.SetArchivedAsync(id, request.ConcurrencyStamp, false, cancellationToken));

    private static async Task<IResult> Import(IFormFile file, IProductService service, CancellationToken cancellationToken)
    {
        if (file.Length == 0 || file.Length > 5_000_000)
            return Results.BadRequest(new { error = "CSV must contain data and be no larger than 5 MB." });
        await using var stream = file.OpenReadStream();
        return Results.Ok(await service.ImportCsvAsync(stream, cancellationToken));
    }

    private static IResult Export(IProductService service, string? search, ProductType? type,
        bool includeArchived = false, bool? tracksInventory = null, string sort = "name", bool descending = false,
        CancellationToken cancellationToken = default) => Results.Stream(
            stream => service.ExportCsvAsync(new(search, type, includeArchived, tracksInventory, sort, descending, 1, 100), stream, cancellationToken),
            "text/csv; charset=utf-8", "products.csv");

    private static async Task<IResult> Execute<T>(Func<Task<T>> operation, bool created = false)
    {
        try
        {
            var value = await operation();
            return created && value is ProductDto product
                ? Results.Created($"/api/v1/products/{product.Id}", value)
                : Results.Ok(value);
        }
        catch (ProductValidationException exception) { return Results.ValidationProblem(exception.Errors); }
        catch (ProductConcurrencyException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (ProductConflictException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }
}

public sealed record ProductConcurrencyRequest(string ConcurrencyStamp);
