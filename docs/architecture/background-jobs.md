# Background jobs

Modules register stable, dot-qualified job names through `IBackgroundJobRegistry`, implement `IBackgroundJobHandler<TPayload>`, and enqueue work through `IBackgroundJobScheduler`. They do not depend on the PostgreSQL worker or a third-party scheduler. `SampleModule` registers `erp.sample.heartbeat` as the executable reference.

Queued jobs are durable records in `platform.background_jobs`. Each record carries tenant, company, serialized payload, correlation ID, due time, attempt limits, lease state, and an optional idempotency key. The scheduler validates the registered payload type and returns the existing job identifier when the same company, job name, and idempotency key is submitted again.

The hosted worker claims due jobs with one PostgreSQL `UPDATE` using `FOR UPDATE SKIP LOCKED`. This permits multiple application instances to compete safely without executing the same active lease. Before resolving the module handler, the worker establishes its tenant/company execution context; module services and scoped DbContexts therefore retain normal tenant filters.

Failures are logged and stored in bounded form. Retries use exponential delays capped at five minutes. A job becomes `dead_letter` after its configured attempt limit; completed jobs retain their execution history. Handlers remain responsible for idempotent external side effects because a process can fail after a side effect but before recording success—durable queues provide at-least-once, not exactly-once, execution.

Authorized operators can inspect payload-free dead-letter metadata and request an audited replay from **Administration > Queue recovery**. Replay clears lease, failure, retry, and dead-letter state so the normal worker can claim the job again. Recurring schedules and automatic retention cleanup remain future additions; the current abstraction supports immediate and delayed one-off work without coupling modules to Quartz.NET or another scheduler.
