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

The Inventory fulfillment lookup migration builds
`inventory."IX_stock_moves_fulfillment_lookup"` with `CREATE INDEX CONCURRENTLY`.
This avoids holding the normal table-level write-blocking lock during the build,
but it is non-transactional and can leave an invalid index if interrupted. Before
retrying a failed migration, check both `inventory."__EFMigrationsHistory"` and
the index catalog (`pg_index.indisvalid`/`indisready`). If the migration is not
recorded and the named index remains, preserve the migration logs, drop only that
index with `DROP INDEX CONCURRENTLY` in autocommit mode, then rerun the reviewed
migration. If the migration is recorded but the index is invalid, stop rollout
and repair it through a reviewed database change; do not mark the deployment
healthy or edit migration history by hand.

```sql
SELECT migration."MigrationId"
FROM inventory."__EFMigrationsHistory" AS migration
WHERE migration."MigrationId" = '20261025032105_FulfillmentLookup';

SELECT index_class.relname, index_state.indisready, index_state.indisvalid
FROM pg_index AS index_state
JOIN pg_class AS index_class ON index_class.oid = index_state.indexrelid
JOIN pg_namespace AS index_schema ON index_schema.oid = index_class.relnamespace
WHERE index_schema.nspname = 'inventory'
  AND index_class.relname = 'IX_stock_moves_fulfillment_lookup';
```

### Queue lease ownership upgrade

`20261021143000_QueueLeaseOwnership` adds nullable UUID `LeaseId` columns to
`platform.outbox_messages` and `platform.background_jobs`. It does not rewrite
payloads, counters, state, deadlines, keys, or existing business records. Legacy
rows retain null ownership until a new worker claims them; an unexpired legacy
lease remains ineligible until its existing deadline has passed.

This upgrade is **not** safe for mixed old/new workers. Old binaries update queue
results by ID alone and can bypass the new fence. Stop all old application/worker
processes and suspend replay administration, take the normal backup, apply the
reviewed migration with the migration identity, then start only the new version.
Verify readiness, normal delivery, retry/dead-letter behavior, and authorized
replay before resuming traffic. Preserve the existing lease deadlines rather than
manually clearing them to speed up recovery. Turning worker options off requires
a controlled restart; it does not cancel already-running handlers by itself.

If rollback is needed, stop all new workers first and inspect queue state and
consumer side effects. Dropping a lease column discards ownership metadata; it
cannot reverse delivered effects. Use the reviewed release/restore procedure,
never a rolling downgrade alongside active new handlers. Schema compatibility
alone is not a guarantee of safe mixed-version queue behavior.

## Backup and restore drill

1. Record the release version and stop writes or take a transactionally consistent PostgreSQL backup.
2. Back up attachments, Data Protection keys, installed module packages/state, and trusted publisher keys with matching timestamps.
3. Restore into an isolated environment using a separate hostname and credentials.
4. Run migrations, start one instance, and verify readiness, tenant login, attachment download, trial balance, outbox/job depth, and a representative transactional workflow.
5. Record recovery-point and recovery-time results. A backup is not accepted until this restore drill succeeds.

Never restore production Data Protection keys or customer data into an environment accessible to unauthorized staff.

CI performs a logical PostgreSQL backup and restores it into a separate database, then verifies representative Platform, Inventory, and Accounting structures. This catches basic backup incompatibility but does not replace the environment-specific drill above: operators must also prove attachment, key, module-package, credential, DNS, capacity, recovery-time, and recovery-point procedures in an isolated production-like environment.

### Disposable local recovery rehearsal

Run `.\eng\rehearse-recovery.ps1` from the repository root on a Windows host with
Docker Compose, the .NET 10 SDK, and Playwright Chromium available. The script
uses synthetic credentials, loopback-only published ports, and randomly named
Compose projects. Before starting, it refuses project labels that already own
Docker volumes, containers, or networks. It creates one synthetic source
installation and two independent fresh
restore targets, then removes only those uniquely labeled Compose projects and
its temporary work directory. Do not alter the script to point it at a shared or
production Compose project.
This is a functional test harness, not a protected backup system: its temporary
synthetic dump and asset copy are not encrypted and are removed at exit. Never
use it with production data or treat its temporary files as retained backups.

The rehearsal creates synthetic business activity and an attachment, installs
the sample external module, backs up PostgreSQL plus the four persistent
application directories, and restores that set to both fresh targets. The
custom-format dump is checked with `pg_restore --list`, and a separate
intentionally truncated copy must be rejected by that verifier. The restored
targets are started with the current image, which runs the configured
migration check; the script verifies the expected migration history, authenticated
tenant/company context using the source session cookie, attachment bytes, module
state and endpoint, and fulfillment outbox replay without adding an audit record.
It then runs the critical browser workflow—including its multi-company isolation
checks—against the first restore. The script
prints elapsed seconds for source setup/workflow, backup, each restore, and the
overall rehearsal. These local synthetic timings are not production RTO/RPO
measurements; the rehearsal does not measure data loss since a backup point.

| Recovery resource | Authoritative source and coverage | Restore and verification |
| --- | --- | --- |
| PostgreSQL application data, tenant/company identities, user records, module metadata, audit history, jobs, and outbox | PostgreSQL; the rehearsal uses `pg_dump --format=custom` | `pg_restore --exit-on-error` into a new isolated PostgreSQL volume; application readiness, migration history, tenant/company context, and fulfillment replay/audit count are checked |
| Attachment metadata | PostgreSQL | Restored with the database; authenticated download is verified against the original synthetic file's SHA-256 |
| Local attachment bytes | `/var/erp/files` volume when `FileStorage:Provider=Local` | Copy the volume contents into the fresh target before application start; authenticated download verifies the restored object |
| Data Protection key ring | `/var/erp/keys`; keep the same configured application name (`OpenErp`) | Copy keys before starting the restored application; the rehearsal reuses the source authentication cookie, verifying it without logging in again |
| Installed external modules and `modules.state.json` | `/var/erp/modules`; package versions and enabled state are filesystem assets, while module metadata is also in PostgreSQL | Restore the entire directory; startup package validation/loading and the sample module API are verified |
| Trusted module publisher public keys | `/var/erp/trusted-publishers` | Restore alongside module packages and retain the configured signature policy; the local sample uses the explicitly synthetic development policy, not production trust material |
| Deployment settings and secret references | External deployment configuration/secret provider; not in the database or rehearsal backup | Re-provision from the approved secret manager and deployment configuration. Validate database/object-storage credentials, signing policy, scanner, endpoints, TLS/proxy and host settings without putting secret values in the backup |
| S3-compatible object data, if configured instead of local files | External object-storage bucket and its lifecycle/versioning policy | Restore or reattach the corresponding bucket and credentials; verify object availability and metadata association. The local rehearsal uses filesystem storage and does not validate an external S3 recovery |
| Malware scanning, DNS/TLS, proxy, SMTP, telemetry collector, and other external services | External service owners and their independent backup/configuration processes | Restore service access/configuration separately and verify connectivity and security policy. These are not simulated by the local rehearsal |

For an operational rehearsal, record the release/image identifier and synchronized
backup timestamps for PostgreSQL and every asset store. Stop writes or use a
coordinated snapshot before collecting database and filesystem backups; the
local script does not create a cross-resource point-in-time snapshot under
concurrent production writes. Retain and protect backup encryption keys outside
the backup set. Record start/end criteria, elapsed phases, backup age, known
data loss, manual actions, and external dependencies. Establish production RPO
and RTO only from a production-like, authorized rehearsal with the actual
backup, storage, network, secret-provider, and restore-throughput configuration.

#### Local rehearsal record

The full command `.\eng\rehearse-recovery.ps1` completed successfully on
2026-10-25 using Windows, Docker 29.9.2, Compose 5.6.1, and .NET SDK 10.0.113.
It created one synthetic source, restored the database and assets into two fresh
targets, and ran the critical Playwright workflow once against the source and
once against the first restore (both 1 passed, 0 failed). The source workflow
covered Sales, Purchasing, Inventory, Accounting, and multi-company boundaries.
The authenticated source cookie remained valid on both restores; the synthetic
attachment hash matched; the sample module loaded; the expected Inventory
migration was recorded; and replay of one pending fulfillment event did not
increase its Sales audit count. A truncated copy of the PostgreSQL archive was
rejected by `pg_restore --list`.

The recorded backup completed at `2026-10-25T05:05:35Z`. Timings are seconds:

| Phase | Fresh target 1 | Fresh target 2 |
| --- | ---: | ---: |
| PostgreSQL startup and archive restore | 34.86 | 54.34 |
| ERP container preparation and persistent-asset copy | 104.75 | 427.87 |
| Application startup, readiness, and recovery validation | 16.20 | 9.54 |
| Fulfillment replay and audit-count verification (included in validation) | 0.71 | 0.72 |
| Restore to verified application | 155.82 | 491.75 |
| Backup age at verification | 156.19 | 647.94 |

The post-restore browser workflow took 197.43 seconds. Source setup and its
pre-backup workflow took 654.94 seconds, backup capture took 4.13 seconds, and
the entire command took 1509.18 seconds. Asset-stage timing includes `docker
compose create erp` and any image/container preparation as well as copying the
assets; it is not a filesystem-copy-only benchmark. No post-backup writes or
simulated data loss were introduced, so data-loss volume and RPO were not
measured. These local timings include Docker Desktop behavior and synthetic
data only; neither the restore time nor the observed backup age establishes a
production RTO or RPO. The script’s startup/readiness and validation time is
reported as one combined phase, not as separate sub-timings.

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
