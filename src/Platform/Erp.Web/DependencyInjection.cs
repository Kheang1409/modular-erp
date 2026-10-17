using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Web;

public static class DependencyInjection
{
    public static IServiceCollection AddErpWeb(this IServiceCollection services)
    {
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddHttpContextAccessor();
        services.AddScoped(serviceProvider =>
        {
            var navigation = serviceProvider.GetRequiredService<NavigationManager>();
            var configuredBaseAddress = serviceProvider.GetService<IConfiguration>()?["Web:ApiBaseAddress"];
            var baseAddress = new Uri(navigation.BaseUri);
            if (configuredBaseAddress is not null)
            {
                if (!Uri.TryCreate(configuredBaseAddress, UriKind.Absolute, out var configuredUri)
                    || configuredUri.Scheme is not ("http" or "https")
                    || !configuredUri.IsLoopback
                    || configuredUri.UserInfo.Length != 0
                    || configuredUri.Query.Length != 0
                    || configuredUri.Fragment.Length != 0)
                    throw new InvalidOperationException("Web:ApiBaseAddress must be an absolute HTTP or HTTPS loopback URI without credentials, a query string, or a fragment.");
                baseAddress = configuredUri;
            }
            var client = new HttpClient { BaseAddress = baseAddress };
            var cookie = serviceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers.Cookie.ToString();
            if (!string.IsNullOrWhiteSpace(cookie)) client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
            return client;
        });
        return services;
    }
}
