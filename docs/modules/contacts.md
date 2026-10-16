# Contacts module

Contacts owns the `contacts` PostgreSQL schema, its EF Core context and migrations, API, Blazor route, permissions, validation, lifecycle, and CSV interchange foundation.

## Boundaries and isolation

Every row carries both `TenantId` and `CompanyId`. Global query filters require both active contexts and a save guard rejects cross-tenant or cross-company writes. The module references `Erp.Abstractions` and `Contacts.Contracts`; it does not reference platform infrastructure, SampleModule, or another business module's persistence. Consuming modules store a contact identifier and resolve contact data through the public Contacts contracts rather than taking a database dependency.

`Erp.Modules.Contacts.Contracts` is the supported cross-module boundary. `IContactDirectory` resolves one contact or a bounded batch of 100 using the caller's active tenant/company context. It returns immutable `ContactReference` values and never exposes EF entities. The module participates in the platform's shared transaction contract through public persistence abstractions, without taking an Infrastructure dependency. Versioned durable Contacts event payloads remain a separate follow-up; this upgrade does not invent or publish new Contacts events.

The host discovers the module and invokes its public `IModuleDatabaseInitializer` implementation when startup migration is enabled. Production migrations can be applied explicitly:

```powershell
dotnet ef database update --project src/Modules/Contacts --startup-project src/Platform/Erp.Host --context ContactsDbContext
```

## Permissions and endpoints

- `contacts.read`: list and read contacts; UI route `/contacts`
- `contacts.manage`: create and update contacts
- `contacts.archive`: archive and reactivate contacts
- `contacts.import`: `POST /api/v1/contacts/import` with a multipart `file` up to 5 MB
- `contacts.export`: `GET /api/v1/contacts/export`

The collection endpoint supports `search`, `kind`, `isCustomer`, `isSupplier`, `includeArchived`, `sort` (`name`, `created`, or `updated`), `descending`, `page`, and `pageSize` (maximum 100). A contact must be a customer, supplier, or both. Profiles include legal/organization names, website, tax number, billing and shipping addresses, creator/updater identity, and bounded email/phone collections with at most one primary value of each kind. Legacy scalar email and phone fields remain compatibility projections for existing clients. Updates and lifecycle commands require the latest `concurrencyStamp`; stale writes return HTTP 409. Archive is reversible and rows are never physically deleted by the API.

CSV import recognizes `kind` and `displayName` as required headers and the optional `isCustomer`, `isSupplier`, `firstName`, `lastName`, `organizationName`, `legalName`, `email`, `phone`, `website`, `taxNumber`, and `notes` headers. Quoted commas and escaped quotes are supported, uploads are capped at 5 MB and 10,000 rows, and invalid rows are reported while valid rows commit individually. Export pages through the database and streams escaped, spreadsheet-safe rows to the response.

## Audit consistency

Create, update, archive, reactivate, and each imported row write the platform audit trail with before/after JSON. `ContactsDbContext` and `PlatformDbContext` share a scoped connection and explicitly enlist in one PostgreSQL transaction through `IBusinessTransactionCoordinator`. The operation commits only after the business save and audit flush succeed. A failure or cancellation rolls both back and clears the participating trackers; pending platform outbox messages in that transaction are rolled back as well.

Each valid CSV row retains its own transaction; invalid rows do not undo previously committed rows. Nested operations join the outer transaction and cannot independently commit it. An abandoned inner scope makes the outer operation rollback-only. Read operations remain unchanged, and existing tenant/company guards and optimistic concurrency continue to apply. See the [business transaction boundary](../architecture/overview.md#business-transaction-boundary) for ownership and retry limits.
