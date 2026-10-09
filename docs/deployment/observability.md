# Observability and alert runbook

Open ERP exports OTLP traces and metrics. Configure the standard OpenTelemetry exporter variables for an authenticated internal collector; do not expose the collector publicly or attach request bodies, SQL parameter values, job payloads, or outbox payloads as attributes.

## Required dashboard

Create one production dashboard per environment with these panels:

| Panel | Signals |
| --- | --- |
| Availability | readiness success, HTTP request rate, 4xx/5xx rate, latency percentiles |
| PostgreSQL | operation duration, active/idle pool connections, connection-open failures, saturation |
| Outbox | pending count, oldest pending age, dead-letter count |
| Background jobs | pending count, oldest pending age, dead-letter count |
| Runtime | process CPU, working set, allocation rate, GC pause time, thread-pool queue |
| Capacity | database size, filesystem/object-storage utilization, module/key volume utilization |

Filter every panel by environment, service version, and instance. Correlation IDs connect an HTTP request to audit, job, and outbox diagnostics; customer or credential values must not be dashboard labels.

## Minimum alerts

- Readiness fails for two consecutive probe intervals: page the service owner.
- HTTP 5xx ratio breaches the deployment's error budget for five minutes: page and correlate by release.
- Any outbox or background-job dead letter: notify the operator immediately and inspect before replay.
- Oldest pending age rises continuously for ten minutes or exceeds the business delivery objective: investigate consumer health and database leases.
- PostgreSQL connection pool saturation or command latency breaches its service objective: investigate load and query plans before increasing limits.
- Database, attachment, module, or key storage reaches 80%: notify; at 90%: page.
- Migration, backup, restore-rehearsal, or malware-scanner availability failure: block deployment or file uploads as appropriate.

Thresholds are deployment-specific and must be recorded alongside the recovery-time, recovery-point, availability, and queue-delivery objectives. Test alert delivery during each release rehearsal.

## Triage

1. Identify the affected release, environment, tenant-safe operation type, and correlation ID.
2. Confirm readiness and dependency health before replaying work.
3. Inspect failure type and attempt metadata. Retrieve payloads only through controlled database access; the administration UI deliberately omits them.
4. Fix the root cause, confirm the consumer is idempotent, then replay through **Administration > Queue recovery**.
5. Record the incident, replay identifier, outcome, and any threshold or runbook change.
