using System.Threading.RateLimiting;
using System.Net;
using System.Security.Claims;
using Erp.Abstractions.Context;
using Erp.Abstractions.Modules;
using Erp.Infrastructure;
using Erp.ModuleSystem;
using Erp.Modules.Sample;
using Erp.Modules.Contacts;
using Erp.Modules.Products;
using Erp.Modules.Crm;
using Erp.Modules.Sales;
using Erp.Modules.Purchasing;
using Erp.Modules.Inventory;
using Erp.Modules.Accounting;
using Erp.Modules.Reporting;
using Erp.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Erp.Infrastructure.Operations;
using Npgsql;

namespace Erp.Host.Hosting;

public static class HostApplicationExtensions
{
    public static ModuleComposition AddErpHost(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();

        var keyPath = Path.GetFullPath(
            builder.Configuration["DataProtection:KeysPath"] ?? ".erp/keys",
            builder.Environment.ContentRootPath);
        Directory.CreateDirectory(keyPath);
        builder.Services.AddDataProtection()
            .SetApplicationName("OpenErp")
            .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new()
            {
                Title = "Open ERP API",
                Version = "v1",
                Description = "HTTP API for the Open ERP platform and installed modules. Authenticate through the web login before trying protected endpoints."
            });
        });
        builder.Services.AddErpWeb();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 2;
            options.RequireHeaderSymmetry = true;
            foreach (var value in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
                if (IPAddress.TryParse(value, out var address)) options.KnownProxies.Add(address);
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 600,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
            options.AddPolicy("login", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });

        var modules = builder.Services.AddErpModules(
            builder.Configuration, typeof(SampleModule).Assembly, typeof(ContactsModule).Assembly,
            typeof(ProductsModule).Assembly, typeof(CrmModule).Assembly, typeof(SalesModule).Assembly, typeof(PurchasingModule).Assembly, typeof(InventoryModule).Assembly, typeof(AccountingModule).Assembly, typeof(ReportingModule).Assembly);
        modules.Permissions.Add(new(PlatformPermissions.OrganizationRead, "View organization context", "erp.platform"));
        modules.Permissions.Add(new(PlatformPermissions.OrganizationManage, "Create and manage companies", "erp.platform"));
        modules.Permissions.Add(new(PlatformPermissions.SecurityManage, "Manage roles and permissions", "erp.platform"));
        modules.Permissions.Add(new(PlatformPermissions.AuditRead, "Read audit history", "erp.platform"));
        modules.Permissions.Add(new(PlatformPermissions.CurrencyManage, "Manage company currencies and exchange rates", "erp.platform"));
        modules.Permissions.Add(new(PlatformPermissions.AttachmentRead, "Read file attachments", "erp.platform"));
        modules.Permissions.Add(new(PlatformPermissions.AttachmentManage, "Upload file attachments", "erp.platform"));
        modules.Permissions.Add(new(PlatformPermissions.ModuleManage, "Manage trusted server modules", "erp.platform"));
        modules.Permissions.Add(new(PlatformPermissions.OperationsManage, "Recover failed work and clean up completed operational history", "erp.platform"));
        modules.Navigation.Add(new("Organization", "/organization", "Administration", 20, PlatformPermissions.OrganizationRead));
        modules.Navigation.Add(new("Users and access", "/administration/users", "Administration", 21, PlatformPermissions.SecurityManage));
        modules.Navigation.Add(new("Roles and permissions", "/administration/roles", "Administration", 22, PlatformPermissions.SecurityManage));
        modules.Navigation.Add(new("Module manager", "/administration/modules", "Administration", 23, PlatformPermissions.ModuleManage));
        modules.Navigation.Add(new("Queue recovery", "/administration/queues", "Administration", 24, PlatformPermissions.OperationsManage));
        modules.Navigation.Add(new("Data maintenance", "/administration/data-maintenance", "Administration", 25, PlatformPermissions.OperationsManage));
        modules.Navigation.Add(new("Getting started", "/help", "Help", 200));
        modules.Navigation.Add(new("About", "/about", "Help", 201));
        var externalModulePath = Path.GetFullPath(
            builder.Configuration["Modules:ExternalPath"] ?? "modules/external",
            builder.Environment.ContentRootPath);
        var trustedKeysPath = Path.GetFullPath(
            builder.Configuration["Modules:Trust:TrustedKeysPath"] ?? ".erp/trusted-publishers",
            builder.Environment.ContentRootPath);
        var trustedKeys = Directory.Exists(trustedKeysPath)
            ? Directory.GetFiles(trustedKeysPath, "*.pem", SearchOption.TopDirectoryOnly)
                .ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trustPolicy = new ModuleTrustPolicy(
            builder.Configuration.GetValue("Modules:Trust:RequireSignatures", false), trustedKeys);
        builder.Services.AddSingleton<IModulePackageManager>(new ModulePackageManager(new ModuleManifestParser(), externalModulePath, trustPolicy));
        builder.Services.AddErpInfrastructure(builder.Configuration, modules.Permissions);
        builder.Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, Erp.Web.Security.RevalidatingAuthenticationStateProvider>();

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Erp.Host"))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql().AddOtlpExporter())
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddNpgsqlInstrumentation().AddMeter(QueueMetricsCollector.MeterName).AddOtlpExporter());
        return modules;
    }

    public static WebApplication UseErpHost(this WebApplication app)
    {
        app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.Use(async (context, next) =>
        {
            const string correlationHeader = "X-Correlation-ID";
            var supplied = context.Request.Headers[correlationHeader].ToString();
            if (SecurityHeaderPolicy.IsSafeCorrelationId(supplied)) context.TraceIdentifier = supplied;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[correlationHeader] = context.TraceIdentifier;
                SecurityHeaderPolicy.Apply(context.Response.Headers);
                return Task.CompletedTask;
            });
            await next(context);
        });
        app.UseExceptionHandler(handler => handler.Run(context => HandleExceptionAsync(app, context)));
        app.UseStatusCodePages();
        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        return app;
    }

    private static async Task HandleExceptionAsync(WebApplication app, HttpContext context)
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var statusCode = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;
        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            HostLog.UnhandledRequest(app.Logger, context.TraceIdentifier, exception?.GetType().Name ?? "Unknown");
        }

        var title = statusCode < StatusCodes.Status500InternalServerError
            ? "The request is invalid."
            : "An unexpected error occurred.";
        await Results.Problem(title: title, statusCode: statusCode, extensions: new Dictionary<string, object?>
        {
            ["traceId"] = context.TraceIdentifier
        }).ExecuteAsync(context);
    }
}
