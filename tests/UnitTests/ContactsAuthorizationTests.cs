using Erp.Modules.Contacts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class ContactsAuthorizationTests
{
    [Fact]
    public void EveryContactsEndpointDeclaresTheExpectedPermission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<IContactService>(_ => null!);
        var app = builder.Build();
        new ContactsModule().ConfigureEndpoints(app);

        var expected = new Dictionary<(string Route, string Method), string>
        {
            [("/api/v1/contacts/", "GET")] = ContactsPermissions.Read,
            [("/api/v1/contacts/{id:guid}", "GET")] = ContactsPermissions.Read,
            [("/api/v1/contacts/", "POST")] = ContactsPermissions.Manage,
            [("/api/v1/contacts/{id:guid}", "PUT")] = ContactsPermissions.Manage,
            [("/api/v1/contacts/{id:guid}/archive", "POST")] = ContactsPermissions.Archive,
            [("/api/v1/contacts/{id:guid}/reactivate", "POST")] = ContactsPermissions.Archive,
            [("/api/v1/contacts/import", "POST")] = ContactsPermissions.Import,
            [("/api/v1/contacts/export", "GET")] = ContactsPermissions.Export
        };

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        foreach (var endpoint in endpoints.Where(value => value.RoutePattern.RawText?.StartsWith("/api/v1/contacts", StringComparison.Ordinal) == true))
        {
            var method = Assert.Single(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            var permission = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy;
            Assert.Equal(expected[(endpoint.RoutePattern.RawText!, method)], permission);
        }
        Assert.Equal(expected.Count, endpoints.Count(value => value.RoutePattern.RawText?.StartsWith("/api/v1/contacts", StringComparison.Ordinal) == true));
    }
}
