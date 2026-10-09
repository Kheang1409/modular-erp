using Microsoft.AspNetCore.Http;

namespace Erp.Host.Hosting;

public static class SecurityHeaderPolicy
{
    public static void Apply(IHeaderDictionary headers)
    {
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        headers["Content-Security-Policy"] = "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'; object-src 'none'; img-src 'self' data:; font-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'self' ws: wss:";
    }

    public static bool IsSafeCorrelationId(string value) =>
        value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
