# From features to reliability: the next step for Open ERP

Open ERP has reached a useful first milestone: a modular application bringing
together contacts, products, CRM, sales, purchasing, inventory, accounting, and
reporting. It is built with .NET 10, Blazor, PostgreSQL, and Docker, and it remains
a free, open-source community preview under Apache-2.0.

This upgrade focuses on a less visible part of the project: making changes and
their evidence more reliable.

## Why transaction boundaries matter

A business record and its audit entry should not disagree about whether an
operation happened. Previously, a module could save its record and then fail
while saving the platform audit context. The two contexts used separate commits,
even though they pointed at the same PostgreSQL database.

The new shared transaction contract provides a way for a module to enlist its own
context alongside the platform audit and outbox context. Module ownership remains
explicit: modules communicate through public contracts and do not reach into one
another's entities or private schemas.

## Contacts first, then Products

Creating, editing, archiving, or reactivating a contact now commits its business
changes and audit trail together. If the operation fails before commit, both are
rolled back. The same transaction can include staged platform outbox messages,
without claiming that new Contacts event types have already been introduced.

CSV imports keep their existing partial-success behavior: each valid row has its
own transaction, and invalid rows do not undo earlier successful rows. Nested
operations cannot accidentally commit an outer transaction. Cancellation also
triggers rollback and cleanup of participating change trackers.

Products now follows the same boundary for creating, editing, archiving, and
reactivating catalog items. Product changes and audit records commit together;
ordinary CSV imports retain independent transactions per successful row.
Explicit outer transactions can also compose Products and Contacts operations.
This does not introduce new Product integration event types or change the schema.

The shared connection also requires sequential reads. The product edit page now
waits for its record before loading company currencies and units, with a
controlled rendered-form regression guarding against overlapping calls.

These are the first migration slices, not a claim that every business module has
already adopted the new boundary.

## Verification should be real, not a passing no-op

Integration tests now require disposable PostgreSQL and storage containers. If
Docker is unavailable or startup fails, the suite fails visibly instead of
returning early and appearing successful.

That stricter gate exposed an unavailable MinIO container reference on a fresh
hosted runner, even though a cached local image still worked. The storage fixture
now builds from a fixed upstream source revision using pinned official build and
runtime images. It still exercises real S3 operations and remains separate from
the application deployment. CI first warms the same BuildKit cache with visible
build progress; tests still build their own disposable image and run the storage
assertions. A failed source build remains a failed gate.

The Accounting tests no longer accept an external database override. They always
own a fresh disposable database, reducing the risk of accidentally running test
writes against application data.

New transaction regressions exercise real PostgreSQL, real audit persistence,
and real outbox staging. They check database state through fresh contexts after
failures, rather than assuming that an in-memory entity still represents what
was committed.

## CI failures need useful evidence

The pipeline now reports unit, architecture, integration, and configured browser
checks separately. Test-result files and dashboard screenshots are retained as
diagnostic artifacts, and the runner is pinned to an explicit Ubuntu version.

Browser checks now wait for HTTP readiness rather than just a running container.
Failure annotations can include allowlisted setup-phase and HTTP-status labels,
without echoing request bodies, credentials, or exception payloads.

These changes improve failure visibility. They do not turn an unverified hosted
run into a successful one or hide a failed assertion.

## Fresh installations need their own regression coverage

The readiness gate also exposed a first-run bootstrap bug: startup created a
company in a transaction, then queried PostgreSQL for that company before it
had been saved. Base-currency setup now uses the tracked company first, keeping
the existing transaction and database fallback for established companies.

Disposable PostgreSQL regressions cover fresh initialization, repeat startup
without duplicate records or overwritten settings, and currency backfill for an
existing organization. This fix does not change the schema or migrate business
data.

Browser verification on a nondefault Docker port uncovered another deployment
issue: server-rendered pages were calling the browser's published address from
inside the container. Compose now supplies an internal loopback API address, so
Inventory and Reporting do not depend on the external port. Explicit addresses
are restricted to HTTP(S) loopback URLs without credentials, query strings, or
fragments; authentication forwarding and API authorization remain unchanged.

The browser workflow also caught mobile overflow in the company directory. Its
screen-reader-only action heading was positioned outside the table's scroll
container. The layout fix contains that heading without removing its accessible
label or hiding the table's horizontal scrolling.

## What remains before a production release

The next steps are adopting the transaction boundary in the remaining writable
modules, measuring performance with realistic data volumes, completing security
and recovery verification, and strengthening module-upgrade failure recovery.
A complete green CI run and reviewed release notes and version tag remain release
gates. Production deployments also need their own HTTPS, secret management,
least-privilege access, malware scanner, and full backup/restore rehearsal.

Advanced inventory costing, lot/serial tracking, and additional business modules
are separate future extensions, not features this update claims to deliver.

## Help keep the project evolving

Feedback, bug reports, documentation, tests, and code contributions are welcome.
The goal is steady, observable progress: add useful features, test the important
failure cases, and be honest about the project's current limits.

Explore the code, setup instructions, and contribution guidance:
[Open ERP on GitHub](https://github.com/Kheang1409/modular-erp).

#OpenSource #DotNet #Blazor #PostgreSQL #SoftwareArchitecture
