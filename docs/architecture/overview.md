# Architecture overview

Open ERP begins as a modular monolith. `Erp.Host` is the only composition root. Modules implement `IErpModule`, are discovered from explicitly trusted assemblies, and register services, HTTP endpoints, permissions, navigation, and Razor component assemblies before the service provider is built.

The built-in SampleModule remains a compile-time reference, while trusted external modules can be discovered from configured folders at startup. The module system provides strict JSON manifest parsing, SHA-256 inventory verification, assembly/manifest consistency checks, semantic-version/platform checks, dependency-range validation, circular-dependency detection, topological ordering, and permission-manifest consistency. Loading requires restart and is not a security sandbox. See the [module SDK guide](../modules/module-sdk.md).

`Erp.Host/Program.cs` is intentionally limited to composition flow. Host service and middleware setup, account endpoints, platform endpoints, and database/bootstrap initialization live in focused files; this keeps the executable boundary explicit without moving host concerns into business modules.

Platform persistence uses the PostgreSQL `platform` schema. Identity tables, tenants, companies, branches, memberships, and installed-module metadata are migrated there. A default company membership is converted into trusted tenant/company claims during sign-in. Global filters constrain tenant-owned reads; `SaveChanges` rejects tenant-owned writes without a tenant or across tenants. Composite foreign keys also prevent a branch or membership from claiming a different tenant than its company. Trusted bootstrap/migration code creates a context without a request tenant and is the only current guard bypass.

Permission policies are created from the registry at startup and enforced at endpoints/components. Permissions are persisted against tenant-owned roles and resolved from the active tenant's default membership during sign-in. Role-permission updates use Identity's concurrency stamp and the system administrator role cannot lose the security-management permission. User-role replacement validates tenant membership, preserves other tenants' assignments, detects stale snapshots, and prevents removal of the final system-role assignee in a serializable transaction. Assignment changes rotate the user's security stamp; cookie validation runs at one-minute intervals. UI visibility is convenience only; protected APIs and pages carry server-side policy metadata.

Privileged authorization changes write tenant-owned audit records in the same transaction as the mutation. Audit records are append-only through the application persistence boundary and retain actor, company, correlation, network, and before/after context. See [auditing](auditing.md).

Development may migrate on startup. Production defaults to explicit reviewed migration execution. Health is split into process liveness and dependency readiness. OpenTelemetry emits ASP.NET Core, HTTP client, and runtime signals via OTLP.

Development exposes interactive Swagger UI and both Swagger/OpenAPI JSON documents. Production does not expose these endpoints by default.

Company document numbers use a tenant/company-scoped PostgreSQL allocator with atomic row updates and date-token prefix formatting. See [sequences](sequences.md).

Company currency metadata and effective-dated exchange rates are exposed through a platform abstraction with tenant isolation, optimistic concurrency, audit history, and decimal-only rates. See [currencies](currencies.md).

Attachment metadata is tenant/company scoped in PostgreSQL while streamed blob bytes use interchangeable local or S3-compatible providers. See [file storage](file-storage.md).

Modules register typed background-job handlers through public contracts; a durable PostgreSQL worker provides leasing, retries, tenant execution context, correlation, idempotent enqueueing, and dead-letter state. See [background jobs](background-jobs.md).

Typed domain events are dispatched sequentially to scoped module handlers with organization and correlation context. Durable or external delivery is deliberately deferred to the outbox boundary. See [domain events](domain-events.md).

Versioned integration events are inserted beside business changes and leased to tenant-aware consumers by the transactional outbox publisher. See [transactional outbox](transactional-outbox.md).

## Business transaction boundary

`Erp.Abstractions.Persistence` defines BCL-only connection, participant, and
transaction-scope contracts. Infrastructure owns the scoped PostgreSQL connection
and `IBusinessTransactionCoordinator`; a module implements `ITransactionParticipant`
using its own DbContext. Modules never access platform entities or another
module's schema to participate.

Contacts, Products, CRM, Sales, and Purchasing have adopted this boundary. Their mutations share
one physical connection and transaction with platform audit and any staged
outbox messages. CRM also covers stages, assignments, activity completion/reopening,
and lead conversion with both audit entries. Sales includes quotation/order
headers and lines, transactional number allocation, and its existing outbox events.
Purchasing includes purchase-order headers and lines, number allocation, and its
existing confirmation, cancellation, and goods-receipt-request messages.
The coordinator flushes pending
platform changes before committing. Ordinary Contacts and Products CSV imports
preserve independent transactions per valid row.
Other writable modules retain their existing boundaries until explicitly migrated;
this is not a system-wide atomicity claim.

Nested scopes join the outer transaction. An inner commit marks that scope complete
without committing PostgreSQL, while an incomplete inner scope makes the root
rollback-only. A root cannot commit while a child is active. Different connections
or unrelated local transactions are rejected rather than silently replaced.
Root operations also reject pre-existing pending tracked changes, leaving them
untouched instead of committing or discarding work owned by another operation.
Rollback detaches participating transactions and clears their change trackers, even
when the original cancellation token has been cancelled.
If cleanup itself fails, that coordinator cannot be reused; start a fresh scope.

Connections and DbContexts are scoped and must be used sequentially; do not issue
parallel operations on a shared session. Retry a failed request with fresh scoped
services and the operation's idempotency rules, not by transparently replaying a
partially executed callback. A lost commit acknowledgement is an unknown outcome,
not proof that a write failed. This boundary does not make external file/object
storage, module-package filesystem changes, or event-consumer side effects atomic.

## Accepted architecture decisions

The decision identifiers are retained here as a consolidated architecture record.

| Decision | Scope and rationale |
| --- | --- |
| ADR-001: Modular monolith | One deployable application and database keep transactions simple. Explicit module contracts and schema ownership preserve the option of later service extraction. |
| ADR-002: PostgreSQL | PostgreSQL is the production persistence provider. Integration tests exercise the actual provider through Testcontainers. |
| ADR-003: Module contracts | Modules implement `IErpModule` and depend on public abstractions and contracts. Trusted assemblies are composed before building the service provider; hot unloading is excluded. |
| ADR-004: Shared-database tenancy | Tenant identifiers, centralized query filters, cross-tenant write guards, and company-scoped business records isolate organizations and companies in shared schemas. |
| ADR-005: Transactional outbox | Enlisted modules persist integration events in the same PostgreSQL transaction as business changes; remaining writable modules still require explicit adoption. Events are dispatched asynchronously through platform contracts. |
| ADR-006: Blazor | Razor Components provide server-rendered pages and Interactive Server behavior. Module component assemblies participate in host routing. |
| ADR-007: Accounting immutability | Posted journal entries and lines are immutable; corrections use balanced reversals and retain their audit relationship. |

### Accounting correction rules

Corrections create a separately numbered, balanced entry whose lines invert the
original debits and credits. `ReversesEntryId` retains the relationship to the
original entry. Posting and reversal require an open fiscal period and an
idempotency key. Database constraints protect positive exchange rates, one-sided
line amounts, scoped uniqueness, and one reversal per original entry; application
validation additionally requires matching total debits and credits.

Operational documents are editable only while draft. Posting customer invoices,
supplier bills, and payments creates the corresponding journal entry in the same
Accounting database transaction. Payment reversal restores invoice or bill open
balances and creates a compensating entry without editing posted history. See the
[Accounting guide](../modules/accounting.md).
