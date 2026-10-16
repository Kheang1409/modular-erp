# Open ERP

A modular ERP built with .NET 10, Blazor, and PostgreSQL. Open ERP brings company-scoped business workflows, permission-based administration, and visual reporting together in a modular monolith.

## Quick start

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Docker Engine with Compose. The SDK version and roll-forward policy are pinned in [global.json](global.json).

From the repository root:

```powershell
if (-not (Test-Path .env)) { Copy-Item .env.example .env }
# Edit .env: replace POSTGRES_PASSWORD and ERP_ADMIN_PASSWORD before starting.
docker compose up -d --build
```

If `.env` already exists, keep it rather than copying over it. Never commit credentials. The initial build restores NuGet packages and the pinned Bootstrap assets, so it needs network access.

Open [http://localhost:8080](http://localhost:8080) and sign in with the administrator email and password configured in `.env`. The port can be changed with `ERP_PORT`.

The supplied Compose setup is for **local development**, not production. It starts PostgreSQL 18, applies platform and module migrations, and bootstraps the administrator once. While bootstrap credentials remain configured, subsequent startup synchronizes the system Administrator role with registered permissions without resetting the user's password. Remove `ERP_ADMIN_EMAIL` and `ERP_ADMIN_PASSWORD` after initial setup. Named volumes retain the database, files, authentication keys, and external modules; `docker compose down -v` deletes those volumes and must not be used on data you need to keep.

Useful local endpoints:

- `/help` — getting-started workflows
- `/reports/dashboard` — company performance dashboard
- `/health/live` and `/health/ready` — process and database readiness
- `/swagger`, `/swagger/v1/swagger.json`, and `/openapi/v1.json` — Development API documentation; sign in before using protected endpoints

## Business modules

| Module | Capabilities | Guide |
| --- | --- | --- |
| Contacts | Customer and supplier master data | [Contacts](docs/modules/contacts.md) |
| Products | Reusable products, services, categories, and units | [Products](docs/modules/products.md) |
| CRM | Leads, opportunities, and sales pipeline | [CRM](docs/modules/crm.md) |
| Sales | Quotations and sales orders | [Sales](docs/modules/sales.md) |
| Purchasing | Purchase orders and receiving workflows | [Purchasing](docs/modules/purchasing.md) |
| Inventory | Warehouses, locations, movements, balances, and reservations | [Inventory](docs/modules/inventory.md) |
| Accounting | Double-entry ledger, invoices, bills, payments, fiscal controls, and reconciliation | [Accounting](docs/modules/accounting.md) |
| Reporting | Sales, purchasing, inventory, financial statements, aging, cash, and CSV exports | [Reporting](docs/modules/reporting.md) |

Modules own their persistence and communicate through public contracts. For example, `Sales` implements sales behavior while `Sales.Contracts` exposes its integration interfaces and data types; these are intentional boundaries, not duplicate modules.

## Organization and companies

One organization can operate multiple companies in the same database. Users, roles, and installed modules belong to the organization; business records, document sequences, and operating settings are scoped to the selected company.

Use **Administration → Organization** to add or edit companies, then **Users and access** to assign roles, allowed companies, and a default company. The header switcher lists the user's active assigned companies. Creating a company initializes its base currency and grants its creator access; it does not copy another company's business records.

Access changes and password resets invalidate previous sessions. Administration safeguards the final active system administrator and prevents company changes that would leave enabled users without an active company. See the [multi-company guide](docs/architecture/multi-company.md) for setup and permissions.

**Data maintenance** provides a company-specific preview and confirmation for retention cleanup of old successful jobs and processed event messages. It preserves business documents, master data, audit history, failed or pending work, and idempotency-protected jobs. It is not a company reset or a general-purpose data purge.

## Interface and reporting

The responsive Blazor workspace uses locally served Bootstrap 5.3.8 with an application-specific theme. Microsoft Library Manager restores the pinned CSS during the build; no Node.js toolchain or browser connection to a public CDN is required. See [third-party notices](THIRD_PARTY_NOTICES.md) for its MIT license.

Open **Reports → Performance dashboard** for company-specific charts and summaries, then use the detail reports and CSV downloads to reconcile results. Existing report permissions control visibility; the dashboard grants no additional access.

Business-date filters are explicit. Operational charts keep currencies separate, financial statements retain the company's base currency, and stock quantities remain separate by unit. Charts include readable data alternatives, distinguish request failures from empty results, and never substitute demonstration values for missing business activity. Historical stock is shown as of the selected date; reservations reflect currently active reservations. See the [reporting guide](docs/modules/reporting.md) for details.

## Local development

To run the host outside Docker, start PostgreSQL and configure the host separately. `dotnet run` does **not** load the Compose `.env` file. Replace the placeholders below and match the database name, username, password, and published port in your local configuration:

```powershell
docker compose up -d postgres
$env:ConnectionStrings__Postgres = "Host=localhost;Port=5432;Database=erp;Username=erp;Password=REPLACE_WITH_LOCAL_DB_PASSWORD"
# Required only when creating the initial administrator:
$env:BootstrapAdmin__Email = "admin@example.com"
$env:BootstrapAdmin__Password = "REPLACE_WITH_A_LONG_UNIQUE_PASSWORD"
dotnet run --project src/Platform/Erp.Host
```

The default HTTP launch profile uses `http://localhost:5236`. Use process-local development configuration or a secret provider for real credentials, and remove bootstrap settings after setup.

### Build and fast checks

```powershell
dotnet restore Erp.slnx
dotnet build Erp.slnx --no-restore
dotnet test tests/UnitTests/Erp.UnitTests.csproj --no-build
dotnet test tests/ArchitectureTests/Erp.ArchitectureTests.csproj --no-build
dotnet format Erp.slnx --verify-no-changes --no-restore
```

### Database and browser checks

Integration tests require Docker and always create isolated PostgreSQL and S3-compatible storage containers. An unavailable Docker engine or failed container startup fails the tests; it cannot produce a passing no-op. Application connection strings and `ERP_TEST_POSTGRES_CONNECTION` overrides are not used, so the suite cannot be redirected to your application database.

```powershell
dotnet test tests/IntegrationTests/Erp.IntegrationTests.csproj --no-build
```

Browser tests require a running application, Chromium, and all three `ERP_E2E_*` settings. They return early if settings are missing, so an unconfigured green test run is not browser verification. After building, install Chromium and supply credentials for a dedicated test administrator:

```powershell
pwsh tests/EndToEndTests/bin/Debug/net10.0/playwright.ps1 install chromium
$env:ERP_E2E_BASE_URL = "http://localhost:8080"
$env:ERP_E2E_EMAIL = "REPLACE_WITH_TEST_ADMIN_EMAIL"
$env:ERP_E2E_PASSWORD = "REPLACE_WITH_TEST_ADMIN_PASSWORD"
# Read-only desktop/mobile dashboard and form checks:
dotnet test tests/EndToEndTests/Erp.EndToEndTests.csproj --no-build --filter "FullyQualifiedName~DashboardBrowserTests|FullyQualifiedName~FormErrorBrowserTests"
```

Run the complete browser suite only against a disposable test environment: `CriticalWorkflowTests` creates business and administrative records. `dotnet test Erp.slnx --no-build` runs all test projects and requires Docker; browser verification additionally requires the settings above. The [CI workflow](.github/workflows/ci.yml) separates unit, architecture, integration, and configured browser checks, retains test diagnostics, and also exercises module packaging and isolated backup/restore.

## Current milestone and next steps

This is a pre-release community preview, not a production-readiness guarantee. Contacts create, update, archive, and reactivate operations now enlist their business changes and platform audit records in one PostgreSQL transaction. Each valid CSV row still commits independently. See the [transaction boundary](docs/architecture/overview.md#business-transaction-boundary) and the [upgrade article](docs/updates/reliability-and-ci-upgrade.md).

Next release gates are migrating the remaining writable modules to the shared transaction boundary, completing realistic-volume performance and security checks, proving production-like restore and module-upgrade recovery, obtaining a green complete CI run, and publishing reviewed release notes and a version tag. Advanced inventory costing, lots/serials, and additional ERP modules remain separate future extensions.

## Architecture and extension points

- `src/Platform/Erp.Host` — composition root, HTTP endpoints, and startup
- `src/Platform/Erp.Abstractions` — stable module and platform service contracts
- `src/Platform/Erp.Core` and `Erp.Infrastructure` — platform behavior, PostgreSQL, Identity, authorization, storage, jobs, outbox, and health
- `src/Platform/Erp.ModuleSystem` — manifest validation, dependency ordering, discovery, and trusted module lifecycle
- `src/Platform/Erp.Web` — shared Blazor layout, navigation, and administration
- `src/Modules` — business implementations and their public contract assemblies
- `src/Shared/Erp.SharedKernel` — shared identifier types
- `src/Tools/Erp.Cli`, `templates/Erp.ModuleSdk`, and `samples` — packaging tools, module template, and reference modules

Start with the [architecture overview](docs/architecture/overview.md), [authorization matrix](docs/security/authorization-matrix.md), and [threat model](docs/security/threat-model.md). Platform guides cover [document sequences](docs/architecture/sequences.md), [currencies](docs/architecture/currencies.md), [file storage](docs/architecture/file-storage.md), [background jobs](docs/architecture/background-jobs.md), [domain events](docs/architecture/domain-events.md), and the [transactional outbox](docs/architecture/transactional-outbox.md).

To create and package a module beneath this repository:

```powershell
dotnet new install ./templates/Erp.ModuleSdk
dotnet new erp-module -n Payroll -o modules/Payroll --ModuleId community.payroll
./eng/pack-module.ps1 -Project modules/Payroll/Payroll.csproj
```

The generated project references `Erp.Abstractions`. External modules are trusted server code, not sandboxed plugins; review their publisher and source before installing them. Lifecycle changes require a host restart. See the [SDK and packaging guide](docs/modules/module-sdk.md), [manifest rules](docs/modules/module-manifests.md), and [sample tutorial](docs/modules/sample-module.md).

## Production, contribution, and license

Do not expose the development Compose configuration publicly. Production needs HTTPS, secret management, persisted keys and files, reviewed module publishers, least-privilege database access, monitoring, and tested backups. Startup migration is disabled by default in Production; apply reviewed platform **and module** migrations through a controlled release process. Follow the [production operations guide](docs/deployment/production.md) and [observability runbook](docs/deployment/observability.md).

Read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing changes. Report vulnerabilities privately according to [SECURITY.md](SECURITY.md). Compatibility rules are in the [development guide](docs/development/compatibility.md), and unreleased changes are recorded in [CHANGELOG.md](CHANGELOG.md).

Open ERP is licensed under the [Apache License 2.0](LICENSE). Third-party components retain their own licenses as described in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
