# Background jobs

Modules register stable, dot-qualified job names through `IBackgroundJobRegistry`, implement `IBackgroundJobHandler<TPayload>`, and enqueue work through `IBackgroundJobScheduler`. They do not depend on the PostgreSQL worker or a third-party scheduler. `SampleModule` registers `erp.sample.heartbeat` as the executable reference.

Queued jobs are durable records in `platform.background_jobs`. Each record carries tenant, company, serialized payload, correlation ID, due time, attempt limits, lease state, and an optional idempotency key. The scheduler validates the registered payload type and returns the existing job identifier when the same company, job name, and idempotency key is submitted again.

The hosted worker claims due jobs with one PostgreSQL `UPDATE` using `FOR UPDATE SKIP LOCKED`. Multiple instances can compete for work without claiming the same unexpired lease, but a handler can continue running after its lease expires and another worker reclaims the job. Before resolving the module handler, the worker establishes its tenant/company execution context; module services and scoped DbContexts therefore retain normal tenant filters.

Each claim stores a fresh GUID `LeaseId`. A result changes the row only while that
token still owns the same tenant/company job in `running` state. Success, retry,
and dead-letter results clear ownership; a stale result is ignored and logged as
lease lost, not as accepted completion/failure. A still-owning handler may finish
after the deadline if no replacement has claimed it. Expiry keeps its existing
strict comparison and retry limits; it does not terminate the old handler or
fence its external side effects. This change does not add lease heartbeats or
handler execution timeouts.

Replay performs a conditional dead-letter-to-queued transition inside the shared
business/audit transaction, invalidates old ownership, and preserves the job ID,
payload, idempotency key, and configured attempt limit. Competing requests cannot
both reset the same eligible state. Missing/ineligible replay returns false and
still completes its nested scope, so it cannot poison a caller's outer work.
Failures/cancellation before commit roll back state and audit together. This does
not make unrelated scheduler or handler side effects atomic with replay.

The nullable lease column is additive. Follow the [queue upgrade procedure](../deployment/production.md#queue-lease-ownership-upgrade):
legacy active leases keep their deadlines and are assigned a new token only when
reclaimed. Old worker binaries must not overlap token-fenced workers.

Failures are logged and stored in bounded form. Retries use exponential delays capped at five minutes. A job becomes `dead_letter` after its configured attempt limit; completed jobs retain their execution history. Handlers remain responsible for idempotent external side effects because a process can fail after a side effect but before recording success—durable queues provide at-least-once, not exactly-once, execution.

Authorized operators can inspect payload-free dead-letter metadata and request an audited replay from **Administration > Queue recovery**. Replay clears lease, failure, retry, and dead-letter state so the normal worker can claim the job again. Recurring schedules and automatic retention cleanup remain future additions; the current abstraction supports immediate and delayed one-off work without coupling modules to Quartz.NET or another scheduler.
