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

## Contacts, Products, and now CRM

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

CRM is the next adoption. Its lead and opportunity workflows, pipeline-stage
configuration, assignments, and activity creation/completion/reopening now commit
with their platform audit records. Lead conversion is especially important: the
source-lead update, new opportunity, and both audit entries belong to one
transaction, even though the workflow flushes audit records twice.

Failures after either conversion flush roll back the complete operation. Return
data is resolved before commit as well, so a late stage-lookup failure cannot
leave a committed assignment behind. The upgrade keeps existing permissions and
business-state rules and requires no schema migration or new CRM event types.

Real directory tests also exposed an assignment-query issue: company-user lookup
filtered a projected record that PostgreSQL's EF provider could not translate.
Filtering and ordering now happen before the final record projection, retaining
the same tenant/company membership checks while making assignment usable.

## Sales: documents, numbers, and events together

Sales now joins the same transaction boundary. Creating or editing a quotation,
changing its state, archiving/reactivating it, converting it into an order, and
confirming/cancelling an order commit with their audit records and applicable
outbox events. The existing five versioned Sales event contracts stay unchanged.

Document numbering is part of that operation too. If a transaction rolls back
after allocation or an audit flush, the document, its lines, its sequence changes,
and its audit/outbox records roll back together. This is not a promise of exactly-once
event delivery or a legal gapless-numbering guarantee; the outbox keeps its existing
delivery semantics.

The upgrade preserves server-calculated totals, customer/address snapshots,
optimistic concurrency, and the database uniqueness rule for quotation conversion.
Product lookup also respects its directory boundary: sequential batches of 100 IDs
support the existing 500-line limit without making an oversized directory request.
No database migration or permission expansion is required.

The PostgreSQL checks also exposed a real draft-editing defect: replacement lines
with preassigned IDs were being treated as existing rows, producing a false
concurrency error. Explicitly staging those lines as new rows fixes the edit path;
regressions exercise repeated replacements and failures after deleting old lines.
Sales detail tables now use the application's named, keyboard-accessible scrolling
container to keep wide line items usable on mobile.

## Purchasing: one outcome for orders and their evidence

Purchasing now follows the same shared transaction boundary. Creating and editing
purchase orders, sending/confirming/cancelling them, and archiving/reactivating
eligible orders commit with their numbering, lines, audit records, and applicable
outbox messages. Confirmation stages both existing messages: order confirmation
and goods-receipt request. Cancellation retains its existing event contract.

A failure after number allocation, line replacement, or a platform flush rolls back
the whole operation. Supplier identity, tax/address facts, currency, calculated
totals, and line snapshots keep their existing meaning. Product lookups use
sequential 100-ID batches within the existing 500-line limit, and replacement lines
are explicitly staged as new rows to avoid false edit-concurrency failures.

This changes no schema, permission, event payload, or state-transition rule. A
receipt-request message is not a completed Inventory receipt, and it does not make
downstream consumer effects atomic with the purchase order. Inventory and Accounting
adoption are described below. The existing outbox delivery semantics and numbering
caveats still apply.

Purchase-order lists and details also use named, keyboard-accessible scroll regions
to contain wide rows on mobile. PostgreSQL regressions and an authenticated browser
lifecycle check protect both the persistence boundary and the rendered workflow.

## Inventory: stock safety and one durable outcome

Inventory now shares its ledger/configuration/reservation transaction with platform
audit and existing outbox events. Warehouse and location changes, movement drafts
and batches, completion, compensating corrections, reservation creation/release,
and delivery consumption all use the explicit shared boundary.

The existing advisory stock/document/key locks and Read Committed isolation remain
in place. Completed movements are still immutable. Correcting history creates a
separately numbered draft compensating movement, not an edit or deletion of the
original ledger entry. Number allocation and every batch audit flush now join the
same transaction, so a late failure cannot leave half a batch or a consumed
reservation committed without its movement evidence.

The review also exposed a reservation-release race: a request could pass its version
check, wait for the stock lock, then overwrite a newer release. Rechecking the record
under the lock preserves a same-key replay while rejecting a different-key stale
writer without another release audit/event. Successful replay paths also complete
their nested transaction scope, keeping caller-owned work intact.
Completion retries now normalize keys the same way they are stored, avoiding a
false concurrency error when an accepted key contains surrounding whitespace.

All nine existing Inventory event contracts, quantity/location rules, permissions,
and schema stay unchanged. Fulfillment messages do not introduce a new consumer
that automatically changes Sales or Purchasing document state. The at-least-once
delivery and numbering caveats still apply; Accounting adoption is described next.
Populated movement, balance, reservation, warehouse, and location tables now use the same named,
keyboard-accessible mobile scrolling pattern as Sales and Purchasing.
Correction navigation also resets the detail page's busy state so the newly
created draft's actions remain usable without a page reload.
The browser check also caught a real filter-input failure: component event binding
was passing `ChangeEventArgs` to a string callback. Inventory movement, batch, and
warehouse filters now use native input binding, with typed-filter browser checks
guarding the affected screens.
Selected products and locations remain visible while filtering, preventing a
placeholder from hiding a still-bound value that would otherwise be submitted.

## Accounting: a posting and its evidence must agree

Accounting now adopts the same explicit boundary for all twenty write methods.
Account, period, and journal configuration, ledger drafts/posting/reversals,
invoices, bills, payments and allocations, tax codes, exchange rates, and bank
reconciliations commit with their audit records and applicable existing events.

The important correction is where commit happens. Invoice/bill and payment
posting previously committed their Accounting transaction before saving platform
audit and outbox records. The boundary now begins before reads or staged changes
and commits only after the operation's evidence is persisted. A failed audit flush,
partial event staging, cancellation, or late allocation validation cannot leave a
posted entry or changed paid balance behind. Successful no-op/replay paths also
complete their nested scope without undoing a caller's outer transaction.

This is not a rewrite of the accounting model. Posted journal entries and lines
remain immutable, reversals remain compensating entries, source orders stay behind
public contracts, and money rounding, currency snapshots, permissions, existing
key/state rules, and all six event contracts are retained. Accounting's existing
date/GUID numbers are not silently converted to sequences or a legal gapless policy.
Read Committed and optimistic concurrency remain in place; this is not a new
serialization guarantee between posting and simultaneous period closure, nor a
promise that all concurrent duplicate-key requests succeed as replays.

The UI review found another practical problem: invoice and receipt screens inferred
account roles from list order, which could settle a different receivable account.
Posting now requires explicit journal/account selections. Users can review bank,
receivable/payable, revenue/expense, and applicable tax choices before submitting.
Journal-line controls have accessible labels, and populated Accounting tables use
named keyboard-focusable scroll regions for desktop and phone-sized layouts.
The browser workflow also exposed a list/detail mismatch: an unreversed entry's
list row used an empty GUID where the individual endpoint returned null, hiding
the Reverse action. The list now returns the same nullable link without modifying
posted history.

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

Multi-company browser checks also retain the original workflow exception when
cleanup fails, rather than replacing it with the cleanup error. Company navigation
reports unsuccessful HTTP status before waiting for the interactive page. These
diagnostics do not bypass authentication, raise rate limits, or suppress failures.

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

The browser checks also follow the drawer's current Open/Close navigation labels
and cover visible keyboard-focus wrapping, scrim/Escape closure, and focus
restoration. Navigation waits for each new interactive circuit before the next
click: rendered server HTML alone does not prove an interactive page is ready.
URL assertions also check navigation state directly instead of waiting for an
event that may already have fired. This avoids reporting a timeout after a
successful save; rendered headings and persisted-state assertions remain in place.

## What remains before a production release

The next steps are reviewing any remaining platform/extension write boundaries,
measuring reporting and performance with realistic data volumes, completing security
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
