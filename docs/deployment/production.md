# Production operations

## Required configuration

- Run behind HTTPS and set `Security__RequireSecureCookies=true`.
- Configure each trusted proxy IP through `ReverseProxy__KnownProxies__0`, `__1`, and so on. Forwarded headers from other sources are ignored.
- Persist and back up `/var/erp/keys`, `/var/erp/files`, `/var/erp/modules`, and PostgreSQL. Restrict all four to the application/operator identities that require them.
- Use a dedicated least-privilege PostgreSQL login. A migration identity may own schemas; the runtime identity should receive only the DML and sequence privileges it needs.
- Store secrets in the deployment secret provider, never in an image, repository, or committed environment file.
- Set `Modules__Trust__RequireSignatures=true` and provision reviewed publisher public keys only.
- Provide an `IFileMalwareScanner` implementation and set `FileStorage__RequireMalwareScanner=true` before enabling untrusted uploads.
- Send OTLP data to an authenticated internal collector and ensure attributes do not contain credentials or business payloads.

### PostgreSQL identities

Create separate login roles for controlled migrations and normal application runtime. Supply both passwords through the deployment secret provider; never place them in a command history, checked-in file, or image layer. The migration role owns application schemas and is used only by the release migration job. Set `Database__MigrateOnStartup=false` on every runtime instance.

After migrations create the application schemas, run [`eng/provision-postgres-runtime.sql`](../../eng/provision-postgres-runtime.sql) as an administrator with `database_name`, `migration_role`, and `runtime_role` psql variables. The script grants the runtime login only database connect, schema usage, table DML, and sequence access. It also configures migration-role default privileges so later tables and sequences remain usable without granting schema creation or ownership. Review and extend the schema list when a trusted external module owns an additional schema.

## Deployment and migrations

Back up first, deploy the new artifact, run reviewed migrations once, then start or roll application instances. Startup migrations are serialized by a PostgreSQL advisory lock, but production should normally keep `Database__MigrateOnStartup=false` and run migrations as a controlled release step. EF migrations may be irreversible; application rollback does not imply database rollback.

For rolling deployments, additive schema changes must land before code that requires them. Remove old columns only in a later release after every old instance is gone. Preserve Data Protection keys across instances so authentication cookies remain readable.

## Backup and restore drill

1. Record the release version and stop writes or take a transactionally consistent PostgreSQL backup.
2. Back up attachments, Data Protection keys, installed module packages/state, and trusted publisher keys with matching timestamps.
3. Restore into an isolated environment using a separate hostname and credentials.
4. Run migrations, start one instance, and verify readiness, tenant login, attachment download, trial balance, outbox/job depth, and a representative transactional workflow.
5. Record recovery-point and recovery-time results. A backup is not accepted until this restore drill succeeds.

Never restore production Data Protection keys or customer data into an environment accessible to unauthorized staff.

CI performs a logical PostgreSQL backup and restores it into a separate database, then verifies representative Platform, Inventory, and Accounting structures. This catches basic backup incompatibility but does not replace the environment-specific drill above: operators must also prove attachment, key, module-package, credential, DNS, capacity, recovery-time, and recovery-point procedures in an isolated production-like environment.

## Monitoring and alerts

Probe `/health/live` for process liveness and `/health/ready` for PostgreSQL readiness. Alert on readiness failures, HTTP 5xx rate, authentication lockouts, outbox/job dead letters, growing queue age/depth, migration failures, storage failures, and disk/database capacity. Use `X-Correlation-ID` to connect HTTP, audit, job, and outbox diagnostics.

HTTP server/client spans and Npgsql command/connection spans are exported through OTLP. Runtime, HTTP, Npgsql, and queue metrics use the same configured collector. Database spans retain command timing and database context; do not enrich them with parameter values or application payloads.

The `Erp.Operations` meter exports `erp.outbox.pending`, `erp.outbox.dead_letters`, `erp.jobs.pending`, `erp.jobs.dead_letters`, `erp.outbox.oldest_pending_age`, and `erp.jobs.oldest_pending_age`. Alert thresholds must reflect normal workload, but any dead letter and a continuously increasing oldest-pending age require investigation.

Application logs record identifiers, event/job types, attempt counts, correlation IDs, and failure type names. They intentionally exclude request bodies, job/outbox payloads, exception messages, passwords, tokens, and connection strings.

## Recovery

- Package replacement keeps a hidden backup until state is saved. A caught pre-commit failure attempts to restore the previous package and preserves recovery data if that attempt fails. A post-commit backup-cleanup failure leaves the new package committed and any remaining hidden backup files for inspection; recursive cleanup may already have removed part of that old backup. See the [module recovery boundary](../modules/module-sdk.md#failure-and-recovery-boundary).
- Before recovering packages, stop all module writers and application instances using that root. Inspect the visible package, `modules.state.json`, hidden `.id.backup-*` directories, and `.staging` together. Preserve a copy of the evidence; select a reviewed matching package/state pair, validate it with the configured publisher policy, then restart one instance. Do not blindly remove state (missing flags enable packages), restore an arbitrary backup, or treat package rollback as database-migration rollback.
- Package filesystem changes and web lifecycle audit records commit separately. If the request fails after package commit, inspect actual package/state before retrying. Named-mutex coordination covers cooperating processes using the same normalized root on one host, not multi-host shared storage or physical-path aliases. Process/power failure recovery is not transactional; an abandoned-mutex warning is not a durable recovery gate.
- A failed module migration stops startup and is not recorded as successful. Repair forward from the database backup and migration evidence; do not promise automatic rollback.
- Dead-letter records must be inspected for root cause and payload safety before replay. Replay consumers must remain idempotent.
- Authorized operators can inspect payload-free dead-letter metadata and request audited replay from **Administration → Queue recovery**. The original payload is never rendered in that page.
- Rotate any disclosed database, object-storage, SMTP, signing, or bootstrap credential immediately.

## Retention and maintenance

- Define legal and business retention separately for audit records, posted accounting records, attachments, completed jobs, processed outbox messages, and dead letters. Never apply a generic purge to immutable financial history.
- Automate PostgreSQL backups and verify them. Monitor autovacuum, transaction ID age, index growth, slow queries, storage capacity, replication lag where applicable, and supported minor-version updates.
- Remove processed job/outbox rows only through an approved, company-safe retention operation after observability and audit requirements are met. Never remove pending, leased, or dead-letter rows automatically.
- Apply object-storage lifecycle rules only after attachment metadata retention and legal-hold requirements are defined. Reconcile metadata and blobs before permanent deletion.
- Back up Data Protection keys with access controls and retention at least as long as cookies or protected payloads must remain decryptable.

See [observability and alert runbook](observability.md) for required dashboards, alerts, and triage behavior.
