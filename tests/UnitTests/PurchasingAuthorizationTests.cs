using Erp.Modules.Purchasing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
namespace Erp.UnitTests; public sealed class PurchasingAuthorizationTests { [Fact] public void EveryEndpointHasPermission() { var b = WebApplication.CreateBuilder(); b.Services.AddAuthorization(); b.Services.AddScoped<IPurchaseOrderService>(_ => null!); var app = b.Build(); new PurchasingModule().ConfigureEndpoints(app); var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().Where(x => x.RoutePattern.RawText?.StartsWith("/api/v1/purchasing", StringComparison.Ordinal) == true).ToArray(); Assert.Equal(9, endpoints.Length); Assert.All(endpoints, x => Assert.False(string.IsNullOrWhiteSpace(Assert.Single(x.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy))); } }
