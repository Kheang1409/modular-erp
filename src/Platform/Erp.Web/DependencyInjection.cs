using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
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
            var client = new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };
            var cookie = serviceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers.Cookie.ToString();
            if (!string.IsNullOrWhiteSpace(cookie)) client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
            return client;
        });
        return services;
    }
}
