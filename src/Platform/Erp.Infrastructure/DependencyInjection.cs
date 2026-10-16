using Erp.Abstractions.Context;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Security;
using Erp.Abstractions.Sequences;
using Erp.Abstractions.Currencies;
using Erp.Abstractions.Files;
using Erp.Core;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Security;
using Erp.Infrastructure.Sequences;
using Erp.Infrastructure.Currencies;
using Erp.Infrastructure.Files;
using Amazon.Runtime;
using Amazon.S3;
using Erp.Abstractions.Jobs;
using Erp.Infrastructure.Jobs;
using Erp.Abstractions.Events;
using Erp.Infrastructure.Events;
using Erp.Abstractions.Operations;
using Erp.Abstractions.Persistence;
using Erp.Infrastructure.Operations;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Erp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddErpInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IPermissionRegistry permissions)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");

        services.AddScoped<IRelationalDatabaseSession>(_ => new RelationalDatabaseSession(connectionString));
        services.AddDbContext<PlatformDbContext>((serviceProvider, options) => options.UseNpgsql(
            serviceProvider.GetRequiredService<IRelationalDatabaseSession>().Connection));
        services.AddScoped<IBusinessTransactionCoordinator, BusinessTransactionCoordinator>();
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
        })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<PlatformDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();
        services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, ErpUserClaimsPrincipalFactory>();
        services.AddScoped<IAuthenticatedSessionValidator, AuthenticatedSessionValidator>();
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme).Configure(options =>
        {
            options.LoginPath = "/account/login";
            options.AccessDeniedPath = "/account/denied";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = configuration.GetValue("Security:RequireSecureCookies", false)
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = CompanyCookieValidation.ValidateAsync;
            options.Events.OnRedirectToLogin = context => RedirectOrStatusAsync(context, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => RedirectOrStatusAsync(context, StatusCodes.Status403Forbidden);
        });
        services.AddAuthorizationBuilder();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        foreach (var permission in permissions.Permissions)
        {
            services.AddAuthorizationBuilder().AddPolicy(permission.Name, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission.Name)));
        }

        services.AddHttpContextAccessor();
        services.AddScoped<ExecutionOrganizationContext>();
        services.AddScoped<ITenantContext>(services => services.GetRequiredService<ExecutionOrganizationContext>());
        services.AddScoped<ICompanyContext>(services => services.GetRequiredService<ExecutionOrganizationContext>());
        services.AddScoped<IOrganizationContextReader, OrganizationContextReader>();
        services.AddScoped<IOrganizationAdministrationService, OrganizationAdministrationService>();
        services.AddScoped<Erp.Abstractions.Organization.ICompanyUserDirectory, CompanyUserDirectory>();
        services.AddScoped<IRoleAdministrationService, RoleAdministrationService>();
        services.AddScoped<IUserAdministrationService, UserAdministrationService>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<IAuditLogWriter>(services => services.GetRequiredService<AuditLogService>());
        services.AddScoped<IAuditLogReader>(services => services.GetRequiredService<AuditLogService>());
        services.AddScoped<IAuthenticationAuditService, AuthenticationAuditService>();
        services.AddScoped<ISequenceService, PostgresSequenceService>();
        services.AddScoped<ICurrencyConfigurationService, CurrencyConfigurationService>();
        services.Configure<FileStorageOptions>(configuration.GetSection(FileStorageOptions.SectionName));
        services.TryAddSingleton<IFileMalwareScanner, UnavailableFileMalwareScanner>();
        services.AddSingleton<IFileStorage>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileStorageOptions>>().Value;
            if (string.Equals(options.Provider, "Local", StringComparison.OrdinalIgnoreCase))
                return new LocalFileStorage(Path.GetFullPath(options.LocalRootPath));
            if (!string.Equals(options.Provider, "S3", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("FileStorage:Provider must be 'Local' or 'S3'.");
            if (string.IsNullOrWhiteSpace(options.Bucket))
                throw new InvalidOperationException("FileStorage:Bucket is required for the S3 provider.");
            var clientOptions = new AmazonS3Config
            {
                ForcePathStyle = options.ForcePathStyle,
                ServiceURL = options.ServiceUrl,
                AuthenticationRegion = options.Region
            };
            IAmazonS3 client = !string.IsNullOrWhiteSpace(options.AccessKey) && !string.IsNullOrWhiteSpace(options.SecretKey)
                ? new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), clientOptions)
                : new AmazonS3Client(clientOptions);
            return new S3CompatibleFileStorage(client, options.Bucket);
        });
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<IQueueOperationsService, QueueOperationsService>();
        services.AddScoped<IDataMaintenanceService, DataMaintenanceService>();
        services.AddHostedService<QueueMetricsCollector>();
        services.Configure<BackgroundJobOptions>(configuration.GetSection(BackgroundJobOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IBackgroundJobScheduler, PostgresBackgroundJobScheduler>();
        services.AddHostedService<PostgresBackgroundJobWorker>();
        services.AddScoped<IDomainEventDispatcher, InProcessDomainEventDispatcher>();
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.AddScoped<IOutboxWriter, PostgresOutboxWriter>();
        services.AddHostedService<PostgresOutboxWorker>();
        services.AddSingleton<IClock, Erp.Core.SystemClock>();
        services.AddHealthChecks().AddDbContextCheck<PlatformDbContext>("postgres", tags: ["ready"]);
        return services;
    }

    private static Task RedirectOrStatusAsync(
        Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> context,
        int apiStatusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = apiStatusCode;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }

        return Task.CompletedTask;
    }
}
