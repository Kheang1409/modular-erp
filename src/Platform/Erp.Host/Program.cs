using Erp.Host.Endpoints;
using Erp.Host.Hosting;

var builder = WebApplication.CreateBuilder(args);
var composition = builder.AddErpHost();

var app = builder.Build();
app.UseErpHost();
app.MapStaticAssets();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.DocumentTitle = "Open ERP API";
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Open ERP API v1");
    });
}

app.MapAccountEndpoints();
app.MapPlatformEndpoints();
foreach (var module in composition.Modules.Modules) module.ConfigureEndpoints(app);
app.MapRazorComponents<Erp.Web.App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(composition.Modules.Modules.Select(x => x.ComponentAssembly).ToArray())
    .WithStaticAssets();

await app.InitializeDatabaseAsync(composition.Permissions);
await app.RunAsync();
