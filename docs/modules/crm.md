# CRM module

The first CRM vertical slice owns tenant/company-scoped leads in the `crm` PostgreSQL schema. It includes search, filtering, pagination, create/edit/details UI, assignment fields, expected revenue, probability, contact association, qualification/loss/reopen transitions, archive/reactivate, auditing, and optimistic concurrency.

CRM references Contacts only through `IContactDirectory`. A contact must exist in the active tenant/company and be active when assigned. CRM stores only the contact identifier and never reads or constrains the Contacts schema directly, so historical leads remain intact if a contact is later archived.

Lead transitions are enforced by the application service. Losing a lead requires a reason, reopening clears that reason, and only qualified leads can convert. Converted leads reject editing, reassignment, and further transitions; archive/reactivate remains available. Conversion creates an opportunity, marks the source lead converted, and persists both audit entries in one coordinated PostgreSQL transaction.

Pipeline stages are company-owned, ordered, and uniquely named. Open stages supply server-owned transition probability. Terminal won and lost stages set opportunity state and probability; lost transitions require a reason. Opportunities support search, direct creation, contact association, dedicated assignment, stage movement, and archive/reactivate.

Activities provide a single chronological timeline for either a lead or an opportunity. The database enforces exactly one parent, while the service validates tenant/company visibility, assignees, completion concurrency, and audit history. Company users are resolved through the public `ICompanyUserDirectory`; CRM never references identity persistence. Creation never assigns ownership: only the dedicated assignment operation guarded by `crm.assign` can do that.

Permissions are `crm.read`, `crm.manage`, `crm.assign`, `crm.configure`, and `crm.archive`. Protected APIs are rooted at `/api/v1/crm`; the Blazor UI provides leads, opportunities, activity timelines, opportunity editing/transitions, and pipeline-stage configuration.

## Audit consistency

CRM uses the shared scoped relational session and explicitly enlists through
`IBusinessTransactionCoordinator`. Lead and opportunity creation, editing,
transitions, assignments, archive/reactivate, pipeline-stage configuration, and
activity creation/completion/reopening commit with their audit records. Explicitly
staged platform outbox messages participate in the same transaction; this does
not introduce new CRM event types.

Lead conversion includes the source-lead update, new opportunity, and both audit
flushes. Failure after either flush rolls back all of them. Return-value lookups
also finish before commit, so a failed stage lookup cannot leave a committed
assignment or archive action behind. Rollback clears participating trackers;
stale versions and existing validation rules still reject invalid work.

CRM reads and writes use the shared connection sequentially. Explicit outer
transactions can compose Contacts and CRM work without an inner service call
committing the caller's transaction. Other writable modules still require
adoption; this is not a system-wide atomicity guarantee or a schema migration.
