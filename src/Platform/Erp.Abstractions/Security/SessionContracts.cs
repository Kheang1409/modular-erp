using System.Security.Claims;

namespace Erp.Abstractions.Security;

public interface IAuthenticatedSessionValidator
{
    Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}
