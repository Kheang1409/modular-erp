using Erp.Abstractions.Context;
using System.Security.Claims;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Security;
using Erp.Abstractions.Sequences;
using Erp.Abstractions.Currencies;
using Erp.Abstractions.Files;
using Erp.Abstractions.Jobs;
using Erp.Abstractions.Events;
using Erp.Abstractions.Operations;
using Erp.Infrastructure.Auditing;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Security;
using Erp.Infrastructure.Sequences;
using Erp.Infrastructure.Currencies;
using Erp.Infrastructure.Files;
using Erp.Infrastructure.Jobs;
using Erp.Infrastructure.Events;
using Erp.Infrastructure.Operations;
using Erp.ModuleSystem;
using Erp.Modules.Sample;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Erp.IntegrationTests;

public sealed class PostgresPersistenceTests
{
    [Fact]
    public async Task PlatformModelCreatesAndRoundTripsTenantInPostgres()
    {
        var postgres = await IntegrationContainers.StartPostgresAsync("erp_tests");

        await using (postgres)
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var roleA = Guid.NewGuid();
            var roleB = Guid.NewGuid();
            var userA = Guid.NewGuid();
            var userB = Guid.NewGuid();
            var companyAId = Guid.NewGuid();
            var companyBId = Guid.NewGuid();
            await using (var write = new PlatformDbContext(options))
            {
                await write.Database.EnsureCreatedAsync(CancellationToken.None);
                write.Tenants.AddRange(
                    new Tenant { Id = tenantA, Name = "Tenant A", Slug = "tenant-a", TimeZoneId = "UTC", IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow },
                    new Tenant { Id = tenantB, Name = "Tenant B", Slug = "tenant-b", TimeZoneId = "UTC", IsActive = true, CreatedAtUtc = DateTimeOffset.UtcNow });
                var companyA = CompanyFor(tenantA, "A");
                var tenantBCompany = CompanyFor(tenantB, "B");
                companyA.Id = companyAId;
                tenantBCompany.Id = companyBId;
                write.Companies.AddRange(companyA, tenantBCompany);
                write.Users.AddRange(UserFor(userA, "a@example.test"), UserFor(userB, "b@example.test"));
                write.UserCompanyMemberships.AddRange(
                    MembershipFor(userA, tenantA, companyA.Id),
                    MembershipFor(userB, tenantB, tenantBCompany.Id));
                write.Roles.AddRange(
                    RoleFor(roleA, tenantA, "Manager A", isSystem: true),
                    RoleFor(roleB, tenantB, "Manager B"));
                write.RolePermissions.AddRange(
                    new RolePermissionAssignment { TenantId = tenantA, RoleId = roleA, Permission = "sample.read" },
                    new RolePermissionAssignment { TenantId = tenantB, RoleId = roleB, Permission = "sample.read" });
                write.AuditLogs.AddRange(
                    AuditFor(tenantA, "tenant-a-event"),
                    AuditFor(tenantB, "tenant-b-event"));
                write.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userA, RoleId = roleA });
                await write.SaveChangesAsync(CancellationToken.None);
            }

            await using var tenantAContext = new PlatformDbContext(options, new FixedTenantContext(new TenantId(tenantA)));
            Assert.Equal("A", await tenantAContext.Companies.Select(x => x.Code).SingleAsync(CancellationToken.None));
            Assert.Equal("Manager A", await tenantAContext.Roles.Select(x => x.Name).SingleAsync(CancellationToken.None));
            Assert.Equal(roleA, await tenantAContext.RolePermissions.Select(x => x.RoleId).SingleAsync(CancellationToken.None));
            Assert.Equal("tenant-a-event", await tenantAContext.AuditLogs.Select(x => x.EntityId).SingleAsync(CancellationToken.None));

            var foreignCompany = await tenantAContext.Companies.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantB);
            foreignCompany.Name = "Tampered";
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => tenantAContext.SaveChangesAsync(CancellationToken.None));
            Assert.Contains("Cross-tenant", exception.Message, StringComparison.Ordinal);

            await using var systemContext = new PlatformDbContext(options);
            var companyB = await systemContext.Companies.IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantB);
            systemContext.Branches.Add(new Branch
            {
                Id = Guid.NewGuid(),
                TenantId = tenantA,
                CompanyId = companyB.Id,
                Name = "Invalid branch",
                Code = "INVALID",
                TimeZoneId = "UTC",
                IsActive = true
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => systemContext.SaveChangesAsync(CancellationToken.None));

            await using var tenantARoleContext = new PlatformDbContext(options, new FixedTenantContext(new TenantId(tenantA)));
            var foreignRole = await tenantARoleContext.Roles.IgnoreQueryFilters().SingleAsync(x => x.Id == roleB);
            foreignRole.Name = "Tampered role";
            var roleException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => tenantARoleContext.SaveChangesAsync(CancellationToken.None));
            Assert.Contains("Cross-tenant", roleException.Message, StringComparison.Ordinal);

            await using var auditContext = new PlatformDbContext(options, new FixedTenantContext(new TenantId(tenantA)));
            var auditEntry = await auditContext.AuditLogs.SingleAsync(CancellationToken.None);
            auditEntry.Action = "tampered";
            var auditException = await Assert.ThrowsAsync<InvalidOperationException>(
                () => auditContext.SaveChangesAsync(CancellationToken.None));
            Assert.Contains("append-only", auditException.Message, StringComparison.Ordinal);

            await using var roleServiceContext = new PlatformDbContext(options, new FixedTenantContext(new TenantId(tenantA)));
            var requestContext = new DefaultHttpContext { TraceIdentifier = "audit-test-correlation" };
            requestContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userA.ToString())], "test"));
            var tenantContext = new FixedTenantContext(new TenantId(tenantA));
            var auditLog = new AuditLogService(
                roleServiceContext,
                tenantContext,
                new EmptyCompanyContext(),
                new HttpContextAccessor { HttpContext = requestContext },
                new FixedClock(new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero)));
            var roleService = new RoleAdministrationService(
                roleServiceContext,
                tenantContext,
                new EmptyPermissionRegistry(),
                auditLog);
            var tenantUsers = await roleService.ListUsersAsync(CancellationToken.None);
            var tenantUser = Assert.Single(tenantUsers);
            Assert.Equal(userA, tenantUser.UserId);
            await Assert.ThrowsAsync<ArgumentException>(() => roleService.SetUserRolesAsync(
                userA,
                new SetUserRolesRequest([roleB], [roleA]),
                CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => roleService.SetUserRolesAsync(
                userA,
                new SetUserRolesRequest([], [roleA]),
                CancellationToken.None));
            var createdRole = await roleService.CreateAsync(new CreateRoleRequest("Audited role", []), CancellationToken.None);
            var organizationService = new OrganizationAdministrationService(
                roleServiceContext, tenantContext, new FixedCompanyContext(new CompanyId(companyAId)), auditLog,
                new HttpContextAccessor { HttpContext = requestContext });
            var secondCompany = await organizationService.CreateCompanyAsync(
                userA, new CreateCompanyRequest("Tenant A West", "A-WEST", "USD", "UTC"), CancellationToken.None);
            var tenantCompanies = await organizationService.ListCompaniesAsync(userA, CancellationToken.None);
            Assert.Equal(2, tenantCompanies.Count);
            Assert.All(tenantCompanies, company => Assert.True(company.IsMember));
            Assert.Equal(companyAId, Assert.Single(tenantCompanies, company => company.IsCurrent).Id);
            Assert.False(await organizationService.SwitchCompanyAsync(userA, companyBId, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => organizationService.CreateCompanyAsync(
                userB, new CreateCompanyRequest("Foreign creator", "FOREIGN", "USD", "UTC"), CancellationToken.None));

            var restrictedUserId = Guid.NewGuid();
            var restrictedCompany = CompanyFor(tenantA, "A-RESTRICTED");
            await using (var restrictedSetup = new PlatformDbContext(options))
            {
                restrictedSetup.Users.Add(UserFor(restrictedUserId, "restricted@example.test"));
                restrictedSetup.Companies.Add(restrictedCompany);
                restrictedSetup.UserCompanyMemberships.Add(MembershipFor(restrictedUserId, tenantA, restrictedCompany.Id));
                await restrictedSetup.SaveChangesAsync(CancellationToken.None);
            }
            Assert.Equal(2, (await organizationService.ListCompaniesAsync(userA, CancellationToken.None)).Count);
            var directory = await organizationService.ListCompaniesAsync(userA, CancellationToken.None, includeAllCompanies: true);
            var restrictedSummary = Assert.Single(directory, company => company.Id == restrictedCompany.Id);
            Assert.False(restrictedSummary.IsMember);
            Assert.False(restrictedSummary.IsCurrent);
            Assert.Equal(1, restrictedSummary.MemberCount);
            Assert.False(await organizationService.SwitchCompanyAsync(userA, restrictedCompany.Id, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => organizationService.UpdateCompanyAsync(
                restrictedCompany.Id, new UpdateCompanyRequest(restrictedCompany.Name, restrictedCompany.Code, "UTC", false,
                    restrictedSummary.Version), CancellationToken.None));

            var renamedCompany = await organizationService.UpdateCompanyAsync(secondCompany.Id,
                new UpdateCompanyRequest("Tenant A East", secondCompany.Code, "UTC", true, secondCompany.Version),
                CancellationToken.None);
            Assert.NotNull(renamedCompany);
            Assert.True(renamedCompany.IsMember);
            Assert.NotEqual(secondCompany.Version, renamedCompany.Version);
            await Assert.ThrowsAsync<InvalidOperationException>(() => organizationService.UpdateCompanyAsync(secondCompany.Id,
                new UpdateCompanyRequest("Stale edit", secondCompany.Code, "UTC", true, secondCompany.Version),
                CancellationToken.None));
            var currentSummary = Assert.Single(tenantCompanies, company => company.Id == companyAId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => organizationService.UpdateCompanyAsync(companyAId,
                new UpdateCompanyRequest(currentSummary.Name, currentSummary.Code, "UTC", false, currentSummary.Version),
                CancellationToken.None));
            Assert.True(await organizationService.SwitchCompanyAsync(userA, secondCompany.Id, CancellationToken.None));
            var activeReader = new OrganizationContextReader(roleServiceContext, tenantContext,
                new FixedCompanyContext(new CompanyId(companyAId)));
            var activeOrganization = await activeReader.GetAsync(new UserId(userA), CancellationToken.None);
            Assert.Equal(companyAId, activeOrganization!.CompanyId.Value);
            var renamedOrganization = await organizationService.UpdateOrganizationAsync(
                new UpdateOrganizationRequest("Organization A", "UTC", activeOrganization.TenantVersion), CancellationToken.None);
            Assert.Equal("Organization A", renamedOrganization.Name);
            await Assert.ThrowsAsync<InvalidOperationException>(() => organizationService.UpdateOrganizationAsync(
                new UpdateOrganizationRequest("Stale organization", "UTC", activeOrganization.TenantVersion), CancellationToken.None));

            var inactiveCompany = await organizationService.UpdateCompanyAsync(secondCompany.Id,
                new UpdateCompanyRequest(renamedCompany.Name, renamedCompany.Code, "UTC", false, renamedCompany.Version),
                CancellationToken.None);
            Assert.False(inactiveCompany!.IsActive);
            Assert.False(await organizationService.SwitchCompanyAsync(userA, secondCompany.Id, CancellationToken.None));
            Assert.Equal(companyAId, (await new OrganizationContextReader(roleServiceContext)
                .GetAsync(new UserId(userA), CancellationToken.None))!.CompanyId.Value);
            Assert.Null(await new OrganizationContextReader(roleServiceContext, tenantContext,
                new FixedCompanyContext(new CompanyId(secondCompany.Id))).GetAsync(new UserId(userA), CancellationToken.None));

            var identityServices = new ServiceCollection();
            identityServices.AddLogging();
            identityServices.AddDbContext<PlatformDbContext>(builder => builder.UseNpgsql(postgres.GetConnectionString()));
            identityServices.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>()
                .AddEntityFrameworkStores<PlatformDbContext>();
            await using (var identityProvider = identityServices.BuildServiceProvider())
            await using (var identityScope = identityProvider.CreateAsyncScope())
            {
                var userManager = identityScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var claimsFactory = new ErpUserClaimsPrincipalFactory(userManager, Options.Create(new IdentityOptions()),
                    identityScope.ServiceProvider.GetRequiredService<PlatformDbContext>(),
                    new HttpContextAccessor { HttpContext = requestContext });
                var user = (await userManager.FindByIdAsync(userA.ToString()))!;
                var principal = await claimsFactory.CreateAsync(user);
                Assert.Equal(companyAId.ToString(), principal.FindFirstValue("company_id"));
                Assert.Equal(tenantA.ToString(), principal.FindFirstValue("tenant_id"));
                Assert.True(principal.HasClaim("permission", "sample.read"));

                await organizationService.UpdateCompanyAsync(secondCompany.Id,
                    new UpdateCompanyRequest(inactiveCompany.Name, inactiveCompany.Code, "UTC", true, inactiveCompany.Version),
                    CancellationToken.None);
                requestContext.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, userA.ToString()),
                    new Claim("tenant_id", tenantA.ToString()),
                    new Claim("company_id", companyAId.ToString())], "test"));
                principal = await claimsFactory.CreateAsync(user);
                Assert.Equal(companyAId.ToString(), principal.FindFirstValue("company_id"));
                var anotherUserContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, userB.ToString()),
                        new Claim("company_id", companyAId.ToString())], "test"))
                };
                var anotherFactory = new ErpUserClaimsPrincipalFactory(userManager, Options.Create(new IdentityOptions()),
                    identityScope.ServiceProvider.GetRequiredService<PlatformDbContext>(),
                    new HttpContextAccessor { HttpContext = anotherUserContext });
                principal = await anotherFactory.CreateAsync(user);
                Assert.Equal(secondCompany.Id.ToString(), principal.FindFirstValue("company_id"));
            }
            var updatedAccess = await roleService.SetUserCompaniesAsync(userA,
                new SetUserCompaniesRequest([companyAId, secondCompany.Id], [companyAId, secondCompany.Id], secondCompany.Id),
                CancellationToken.None);
            Assert.Equal(secondCompany.Id, updatedAccess!.DefaultCompanyId);
            Assert.Equal(new[] { companyAId, secondCompany.Id }.OrderBy(id => id), updatedAccess.CompanyIds.OrderBy(id => id));
            var auditPage = await auditLog.ReadAsync(0, 100, CancellationToken.None);
            var createdAudit = Assert.Single(auditPage.Items, x => x.EntityId == createdRole.Id.ToString());
            Assert.Equal("role.created", createdAudit.Action);
            Assert.Equal(userA, createdAudit.UserId);
            Assert.Equal("audit-test-correlation", createdAudit.CorrelationId);

            var authenticationAudit = new AuthenticationAuditService(
                options,
                new HttpContextAccessor { HttpContext = requestContext },
                new FixedClock(new DateTimeOffset(2026, 9, 13, 12, 1, 0, TimeSpan.Zero)));
            Assert.True(await authenticationAudit.RecordSignInAsync(userA, succeeded: true, lockedOut: false, CancellationToken.None));
            Assert.True(await authenticationAudit.RecordLogoutAsync(userA, CancellationToken.None));
            Assert.False(await authenticationAudit.RecordSignInAsync(Guid.NewGuid(), succeeded: false, lockedOut: false, CancellationToken.None));

            var authenticationEntries = await auditLog.ReadAsync(0, 10, CancellationToken.None);
            Assert.Contains(authenticationEntries.Items, x => x.Action == "authentication.sign_in.succeeded" && x.UserId == userA);
            Assert.Contains(authenticationEntries.Items, x => x.Action == "authentication.logout" && x.UserId == userA);
            Assert.Equal(companyAId, Assert.Single(authenticationEntries.Items,
                x => x.Action == "authentication.logout" && x.UserId == userA).CompanyId);

            var sequenceRequest = new SequenceRequest("sales.order", "SO-{yyyy}-", 6, new DateOnly(2026, 9, 13));
            var allocations = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            {
                await using var sequenceContext = new PlatformDbContext(options, tenantContext);
                var service = new PostgresSequenceService(
                    sequenceContext,
                    tenantContext,
                    new FixedCompanyContext(new CompanyId(companyAId)));
                return await service.NextAsync(sequenceRequest, CancellationToken.None);
            }));
            Assert.Equal(20, allocations.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("SO-2026-000001", allocations);
            Assert.Contains("SO-2026-000020", allocations);

            var tenantBContext = new FixedTenantContext(new TenantId(tenantB));
            await using var tenantBSequenceContext = new PlatformDbContext(options, tenantBContext);
            var tenantBSequence = new PostgresSequenceService(
                tenantBSequenceContext,
                tenantBContext,
                new FixedCompanyContext(new CompanyId(companyBId)));
            Assert.Equal("SO-2026-000001", await tenantBSequence.NextAsync(sequenceRequest, CancellationToken.None));

            await using var currencyContext = new PlatformDbContext(options, tenantContext);
            var currencyCompanyContext = new FixedCompanyContext(new CompanyId(companyAId));
            var currencyAudit = new AuditLogService(
                currencyContext, tenantContext, currencyCompanyContext,
                new HttpContextAccessor { HttpContext = requestContext },
                new FixedClock(new DateTimeOffset(2026, 9, 13, 12, 2, 0, TimeSpan.Zero)));
            var currencyService = new CurrencyConfigurationService(
                currencyContext, tenantContext, currencyCompanyContext, currencyAudit,
                new BusinessTransactionCoordinator(currencyContext));
            var usd = await currencyService.ConfigureAsync(
                new ConfigureCurrencyRequest("usd", "US Dollar", "$", 2, true), CancellationToken.None);
            var khr = await currencyService.ConfigureAsync(
                new ConfigureCurrencyRequest("khr", "Cambodian Riel", "៛", 0, true), CancellationToken.None);
            var configuration = await currencyService.SetBaseCurrencyAsync(
                new SetBaseCurrencyRequest("KHR", "USD"), CancellationToken.None);
            Assert.Equal("KHR", configuration.BaseCurrencyCode);
            Assert.Equal(2, configuration.Currencies.Count);
            var rate = await currencyService.AddExchangeRateAsync(
                new AddExchangeRateRequest("USD", "KHR", 4100.25m, new DateOnly(2026, 9, 13)), CancellationToken.None);
            Assert.Equal(4100.25m, rate.Rate);
            await Assert.ThrowsAsync<InvalidOperationException>(() => currencyService.ConfigureAsync(
                new ConfigureCurrencyRequest("USD", "Changed", "$", 2, true, "stale"), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => currencyService.AddExchangeRateAsync(
                new AddExchangeRateRequest("USD", "KHR", 4200m, new DateOnly(2026, 9, 13)), CancellationToken.None));

            await using var isolatedCurrencyContext = new PlatformDbContext(options, tenantBContext);
            var isolatedCurrencyService = new CurrencyConfigurationService(
                isolatedCurrencyContext, tenantBContext, new FixedCompanyContext(new CompanyId(companyBId)),
                new AuditLogService(isolatedCurrencyContext, tenantBContext, new FixedCompanyContext(new CompanyId(companyBId)),
                    new HttpContextAccessor(), new FixedClock(DateTimeOffset.UtcNow)),
                new BusinessTransactionCoordinator(isolatedCurrencyContext));
            Assert.Empty((await isolatedCurrencyService.GetAsync(CancellationToken.None)).Currencies);
            Assert.Equal("USD", (await isolatedCurrencyService.GetAsync(CancellationToken.None)).BaseCurrencyCode);

            var storageRoot = Path.Combine(Path.GetTempPath(), "erp-attachment-tests", Guid.NewGuid().ToString("N"));
            try
            {
                var localStorage = new LocalFileStorage(storageRoot);
                await using var attachmentContext = new PlatformDbContext(options, tenantContext);
                var attachmentAudit = new AuditLogService(
                    attachmentContext, tenantContext, currencyCompanyContext,
                    new HttpContextAccessor { HttpContext = requestContext },
                    new FixedClock(new DateTimeOffset(2026, 9, 13, 12, 3, 0, TimeSpan.Zero)));
                var attachmentService = new AttachmentService(
                    attachmentContext, tenantContext, currencyCompanyContext,
                    new HttpContextAccessor { HttpContext = requestContext },
                    new FixedClock(new DateTimeOffset(2026, 9, 13, 12, 3, 0, TimeSpan.Zero)),
                    localStorage, Options.Create(new FileStorageOptions { MaximumFileSizeBytes = 1024 }), attachmentAudit);
                var bytes = Encoding.UTF8.GetBytes("invoice attachment");
                await using var upload = new MemoryStream(bytes);
                var attachment = await attachmentService.UploadAsync(
                    new UploadAttachmentRequest("invoice.txt", "text/plain", bytes.Length, "Invoice", "INV-1"),
                    upload, CancellationToken.None);
                Assert.Equal("invoice.txt", Assert.Single(await attachmentService.ListAsync("Invoice", "INV-1", CancellationToken.None)).FileName);
                var download = await attachmentService.GetDownloadAsync(attachment.Id, CancellationToken.None);
                Assert.NotNull(download);
                await using var downloaded = new MemoryStream();
                await localStorage.ReadAsync(download.StorageKey, downloaded, CancellationToken.None);
                Assert.Equal(bytes, downloaded.ToArray());

                await using var tenantBAttachmentContext = new PlatformDbContext(options, tenantBContext);
                var tenantBAttachmentService = new AttachmentService(
                    tenantBAttachmentContext, tenantBContext, new FixedCompanyContext(new CompanyId(companyBId)),
                    new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = requestContext.User } },
                    new FixedClock(DateTimeOffset.UtcNow), localStorage,
                    Options.Create(new FileStorageOptions { MaximumFileSizeBytes = 1024 }),
                    new AuditLogService(tenantBAttachmentContext, tenantBContext, new FixedCompanyContext(new CompanyId(companyBId)),
                        new HttpContextAccessor(), new FixedClock(DateTimeOffset.UtcNow)));
                Assert.Null(await tenantBAttachmentService.GetDownloadAsync(attachment.Id, CancellationToken.None));
            }
            finally
            {
                if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true);
            }

            var jobRegistry = new BackgroundJobRegistry();
            jobRegistry.Add<SampleHeartbeatJob, SampleHeartbeatPayload>("erp.sample.heartbeat");
            jobRegistry.Add<FailingJob, FailingPayload>("erp.tests.failing");
            await using var schedulerContext = new PlatformDbContext(options, tenantContext);
            var scheduler = new PostgresBackgroundJobScheduler(
                schedulerContext, tenantContext, currencyCompanyContext, jobRegistry,
                new HttpContextAccessor { HttpContext = requestContext },
                new FixedClock(new DateTimeOffset(2026, 9, 13, 12, 4, 0, TimeSpan.Zero)));
            var scheduledId = await scheduler.EnqueueAsync(
                "erp.sample.heartbeat", new SampleHeartbeatPayload("integration"),
                idempotencyKey: "heartbeat-integration", cancellationToken: CancellationToken.None);
            var duplicateId = await scheduler.EnqueueAsync(
                "erp.sample.heartbeat", new SampleHeartbeatPayload("ignored duplicate"),
                idempotencyKey: "heartbeat-integration", cancellationToken: CancellationToken.None);
            Assert.Equal(scheduledId, duplicateId);

            var workerServices = new ServiceCollection();
            workerServices.AddSingleton(options);
            workerServices.AddLogging();
            workerServices.AddHttpContextAccessor();
            workerServices.AddScoped<ExecutionOrganizationContext>();
            workerServices.AddScoped<SampleHeartbeatJob>();
            workerServices.AddScoped<FailingJob>();
            await using var workerProvider = workerServices.BuildServiceProvider();
            var worker = new PostgresBackgroundJobWorker(
                workerProvider.GetRequiredService<IServiceScopeFactory>(), jobRegistry,
                Options.Create(new BackgroundJobOptions { Enabled = true, LeaseSeconds = 30 }),
                TimeProvider.System, NullLogger<PostgresBackgroundJobWorker>.Instance);
            Assert.True(await worker.ExecuteNextAsync(CancellationToken.None));
            await using var jobCheck = new PlatformDbContext(options);
            var completedJob = await jobCheck.BackgroundJobs.IgnoreQueryFilters().SingleAsync(x => x.Id == scheduledId);
            Assert.Equal("succeeded", completedJob.State);
            Assert.Equal(1, completedJob.Attempts);
            Assert.NotNull(completedJob.CompletedAtUtc);

            var failingId = await scheduler.EnqueueAsync(
                "erp.tests.failing", new FailingPayload("expected"), maxAttempts: 1,
                cancellationToken: CancellationToken.None);
            Assert.True(await worker.ExecuteNextAsync(CancellationToken.None));
            jobCheck.ChangeTracker.Clear();
            var failedJob = await jobCheck.BackgroundJobs.IgnoreQueryFilters().SingleAsync(x => x.Id == failingId);
            Assert.Equal("dead_letter", failedJob.State);
            Assert.Equal("InvalidOperationException", failedJob.LastError);

            await using (var recoveryContext = new PlatformDbContext(options, tenantContext))
            {
                var recoveryAudit = new AuditLogService(
                    recoveryContext, tenantContext, currencyCompanyContext,
                    new HttpContextAccessor { HttpContext = requestContext }, new FixedClock(DateTimeOffset.UtcNow));
                var recovery = new QueueOperationsService(recoveryContext, currencyCompanyContext, new FixedClock(DateTimeOffset.UtcNow), recoveryAudit);
                var snapshot = await recovery.GetAsync(50, CancellationToken.None);
                Assert.Contains(snapshot.Jobs, item => item.Id == failingId);
                Assert.True(await recovery.ReplayJobAsync(failingId, CancellationToken.None));
                Assert.False(await recovery.ReplayJobAsync(failingId, CancellationToken.None));
            }
            jobCheck.ChangeTracker.Clear();
            Assert.Equal("queued", (await jobCheck.BackgroundJobs.IgnoreQueryFilters().SingleAsync(x => x.Id == failingId)).State);

            var outboxRegistry = new IntegrationEventRegistry();
            outboxRegistry.Add<SampleGreetingPublished>("sample.greeting.published.v1");
            var rolledBackEventId = Guid.NewGuid();
            await using (var rollbackContext = new PlatformDbContext(options, tenantContext))
            await using (var transaction = await rollbackContext.Database.BeginTransactionAsync(CancellationToken.None))
            {
                var originalName = await rollbackContext.Companies.Where(x => x.Id == companyAId)
                    .Select(x => x.Name).SingleAsync(CancellationToken.None);
                var company = await rollbackContext.Companies.SingleAsync(x => x.Id == companyAId);
                company.Name = "Rolled back company";
                new PostgresOutboxWriter(
                    rollbackContext, tenantContext, currencyCompanyContext, outboxRegistry,
                    new HttpContextAccessor { HttpContext = requestContext }).Add(
                    new SampleGreetingPublished(rolledBackEventId, DateTimeOffset.UtcNow, originalName));
                await rollbackContext.SaveChangesAsync(CancellationToken.None);
                await transaction.RollbackAsync(CancellationToken.None);
            }
            await using (var rollbackCheck = new PlatformDbContext(options))
            {
                Assert.False(await rollbackCheck.OutboxMessages.IgnoreQueryFilters().AnyAsync(x => x.Id == rolledBackEventId));
                Assert.NotEqual("Rolled back company", await rollbackCheck.Companies.IgnoreQueryFilters()
                    .Where(x => x.Id == companyAId).Select(x => x.Name).SingleAsync(CancellationToken.None));
            }

            var publishedEvent = new SampleGreetingPublished(Guid.NewGuid(), DateTimeOffset.UtcNow, "durable");
            await using (var outboxContext = new PlatformDbContext(options, tenantContext))
            {
                new PostgresOutboxWriter(
                    outboxContext, tenantContext, currencyCompanyContext, outboxRegistry,
                    new HttpContextAccessor { HttpContext = requestContext }).Add(publishedEvent);
                await outboxContext.SaveChangesAsync(CancellationToken.None);
            }
            workerServices.AddScoped<IIntegrationEventConsumer<SampleGreetingPublished>, SampleGreetingPublishedConsumer>();
            await using var outboxProvider = workerServices.BuildServiceProvider();
            var outboxWorker = new PostgresOutboxWorker(
                outboxProvider.GetRequiredService<IServiceScopeFactory>(), outboxRegistry,
                Options.Create(new OutboxOptions { Enabled = true, LeaseSeconds = 30, MaxAttempts = 3 }),
                TimeProvider.System, NullLogger<PostgresOutboxWorker>.Instance);
            Assert.True(await outboxWorker.PublishNextAsync(CancellationToken.None));
            await using var outboxCheck = new PlatformDbContext(options);
            var delivered = await outboxCheck.OutboxMessages.IgnoreQueryFilters().SingleAsync(x => x.Id == publishedEvent.EventId);
            Assert.NotNull(delivered.ProcessedAtUtc);
            Assert.Null(delivered.DeadLetteredAtUtc);
            Assert.Equal(1, delivered.RetryCount);

            var deadLetterId = Guid.NewGuid();
            await using (var deadLetterContext = new PlatformDbContext(options, tenantContext))
            {
                deadLetterContext.OutboxMessages.Add(new OutboxMessage
                {
                    Id = deadLetterId,
                    TenantId = tenantA,
                    CompanyId = companyAId,
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    EventType = "tests.dead-letter.v1",
                    PayloadJson = "{}",
                    CorrelationId = "dead-letter-correlation",
                    NextAttemptAtUtc = DateTimeOffset.UtcNow,
                    DeadLetteredAtUtc = DateTimeOffset.UtcNow,
                    RetryCount = 3,
                    LastError = "expected"
                });
                await deadLetterContext.SaveChangesAsync();
                var recoveryAudit = new AuditLogService(
                    deadLetterContext, tenantContext, currencyCompanyContext,
                    new HttpContextAccessor { HttpContext = requestContext }, new FixedClock(DateTimeOffset.UtcNow));
                var recovery = new QueueOperationsService(deadLetterContext, currencyCompanyContext, new FixedClock(DateTimeOffset.UtcNow), recoveryAudit);
                Assert.True(await recovery.ReplayOutboxAsync(deadLetterId, CancellationToken.None));
                Assert.False(await recovery.ReplayOutboxAsync(deadLetterId, CancellationToken.None));
            }
            outboxCheck.ChangeTracker.Clear();
            var replayed = await outboxCheck.OutboxMessages.IgnoreQueryFilters().SingleAsync(x => x.Id == deadLetterId);
            Assert.Null(replayed.DeadLetteredAtUtc);
            Assert.Equal(0, replayed.RetryCount);
            Assert.Null(replayed.LastError);
        }
    }

    private static Company CompanyFor(Guid tenantId, string code) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Name = $"Company {code}",
        Code = code,
        BaseCurrencyCode = "USD",
        TimeZoneId = "UTC",
        IsActive = true
    };

    private static ApplicationRole RoleFor(Guid id, Guid tenantId, string name, bool isSystem = false) => new()
    {
        Id = id,
        TenantId = tenantId,
        Name = name,
        NormalizedName = name.ToUpperInvariant(),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        IsSystem = isSystem
    };

    private static ApplicationUser UserFor(Guid id, string email) => new()
    {
        Id = id,
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static UserCompanyMembership MembershipFor(Guid userId, Guid tenantId, Guid companyId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        UserId = userId,
        CompanyId = companyId,
        IsDefault = true
    };

    private static AuditLog AuditFor(Guid tenantId, string entityId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        Action = "test.created",
        EntityType = "Test",
        EntityId = entityId,
        TimestampUtc = DateTimeOffset.UtcNow,
        CorrelationId = Guid.NewGuid().ToString("N")
    };

    private sealed class FixedTenantContext(TenantId tenantId) : ITenantContext
    {
        public TenantId? TenantId { get; } = tenantId;
    }

    private sealed class EmptyPermissionRegistry : IPermissionRegistry
    {
        public IReadOnlyCollection<PermissionDefinition> Permissions => [];

        public void Add(PermissionDefinition permission) => throw new NotSupportedException();
    }

    private sealed class EmptyCompanyContext : ICompanyContext
    {
        public CompanyId? CompanyId => null;
    }

    private sealed class FixedCompanyContext(CompanyId companyId) : ICompanyContext
    {
        public CompanyId? CompanyId { get; } = companyId;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed record FailingPayload(string Reason);

    private sealed class FailingJob : IBackgroundJobHandler<FailingPayload>
    {
        public Task ExecuteAsync(FailingPayload payload, BackgroundJobExecutionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(payload.Reason);
    }
}
