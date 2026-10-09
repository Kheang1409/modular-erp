using Erp.Abstractions.Context;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Security;

namespace Erp.Host.Endpoints;

public static class UserAdministrationEndpoints
{
    public static IEndpointRouteBuilder MapUserAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var users = endpoints.MapGroup("/api/v1/platform/users")
            .WithTags("Platform users").RequirePermission(PlatformPermissions.SecurityManage);
        users.MapGet("/", (IRoleAdministrationService service, CancellationToken token) => service.ListUsersAsync(token));
        users.MapPost("/", async Task<IResult> (CreateUserRequest request, IUserAdministrationService service, CancellationToken token) =>
        {
            try
            {
                var user = await service.CreateAsync(request, token);
                return Results.Created($"/api/v1/platform/users/{user.UserId}", user);
            }
            catch (ArgumentException exception) { return Invalid(exception, 400); }
            catch (InvalidOperationException exception) { return Invalid(exception, 409); }
        });
        users.MapPut("/{userId:guid}/active", async Task<IResult> (Guid userId, SetUserActiveRequest request, IUserAdministrationService service, CancellationToken token) =>
        {
            try
            {
                var user = await service.SetActiveAsync(userId, request, token);
                return user is null ? Results.NotFound() : Results.Ok(user);
            }
            catch (ArgumentException exception) { return Invalid(exception, 400); }
            catch (InvalidOperationException exception) { return Invalid(exception, 409); }
        });
        users.MapPost("/{userId:guid}/password", async Task<IResult> (Guid userId, ResetUserPasswordRequest request, IUserAdministrationService service, CancellationToken token) =>
        {
            try
            {
                var user = await service.ResetPasswordAsync(userId, request, token);
                return user is null ? Results.NotFound() : Results.Ok(user);
            }
            catch (ArgumentException exception) { return Invalid(exception, 400); }
            catch (InvalidOperationException exception) { return Invalid(exception, 409); }
        });
        return endpoints;
    }

    private static IResult Invalid(Exception exception, int status) => Results.Problem(
        title: exception.Message, statusCode: status);
}
