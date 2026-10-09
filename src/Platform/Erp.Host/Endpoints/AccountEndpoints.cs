using System.Security.Claims;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Erp.Host.Endpoints;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/account/login", LoginAsync).RequireRateLimiting("login").ExcludeFromDescription();
        endpoints.MapPost("/account/logout", LogoutAsync).RequireAuthorization().ExcludeFromDescription();
        endpoints.MapPost("/account/company/switch", SwitchCompanyAsync).RequireAuthorization().ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> SwitchCompanyAsync(
        [FromForm] SwitchCompanyForm form,
        HttpContext context,
        IOrganizationAdministrationService organizations,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(context.User);
        if (user is null) return Results.Unauthorized();
        if (!await organizations.SwitchCompanyAsync(user.Id, form.CompanyId, cancellationToken))
            return Results.Forbid();
        var originalPrincipal = context.User;
        var selectedPrincipal = originalPrincipal.Clone();
        var selectedIdentity = (ClaimsIdentity)selectedPrincipal.Identity!;
        foreach (var claim in selectedIdentity.FindAll("company_id").ToArray()) selectedIdentity.RemoveClaim(claim);
        selectedIdentity.AddClaim(new("company_id", form.CompanyId.ToString()));
        context.User = selectedPrincipal;
        try { await signInManager.RefreshSignInAsync(user); }
        finally { context.User = originalPrincipal; }
        return Results.LocalRedirect(LocalDestination(form.ReturnUrl));
    }

    private static async Task<IResult> LoginAsync(
        [FromForm] LoginForm form,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthenticationAuditService authenticationAudit,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(form.Email);
        var failureUrl = "/account/login?failed=true&returnUrl=" + Uri.EscapeDataString(LocalDestination(form.ReturnUrl));
        if (user is null) return Results.LocalRedirect(failureUrl);

        var result = await signInManager.CheckPasswordSignInAsync(user, form.Password, lockoutOnFailure: true);
        var audited = await authenticationAudit.RecordSignInAsync(user.Id, result.Succeeded, result.IsLockedOut, cancellationToken);
        if (!result.Succeeded || !audited) return Results.LocalRedirect(failureUrl);

        await signInManager.SignInAsync(user, isPersistent: false);
        return Results.LocalRedirect(LocalDestination(form.ReturnUrl));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        SignInManager<ApplicationUser> signInManager,
        IAuthenticationAuditService authenticationAudit,
        CancellationToken cancellationToken)
    {
        try
        {
            var identifier = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(identifier, out var userId) || !await authenticationAudit.RecordLogoutAsync(userId, cancellationToken))
            {
                throw new InvalidOperationException("The logout audit entry could not be associated with an organization.");
            }
            return Results.LocalRedirect("/");
        }
        finally
        {
            await signInManager.SignOutAsync();
        }
    }

    private static string LocalDestination(string? value) => !string.IsNullOrWhiteSpace(value)
        && value[0] == '/' && (value.Length == 1 || value[1] is not ('/' or '\\')) ? value : "/";
}

public sealed record SwitchCompanyForm(Guid CompanyId)
{
    public string? ReturnUrl { get; init; }
}
