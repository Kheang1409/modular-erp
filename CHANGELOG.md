# Changelog

All notable changes are documented here. The project follows semantic versioning for platform and public module contracts.

## 0.1.0 - unreleased

- Preserved original multi-company browser workflow failures when cleanup also fails, and report unsuccessful company-navigation HTTP status before waiting for an interactive page; authentication and rate limits remain unchanged.
- Extended shared PostgreSQL transactions to all twenty Accounting write methods, including configuration, ledger entries/reversals, invoice/bill posting, payment allocations/reversals, financial controls, audit, and six existing event contracts without changing schema, permissions, or GUID-based number formats.
- Replaced list-order posting-account guesses with explicit journal/account selections for invoices, bills, and payments; added accessible journal-line labels and named keyboard-scrollable Accounting tables.
- Fixed missing ledger-list reversal links being returned as an empty GUID instead of null, restoring the Reverse action for posted entries without altering ledger history.
- Added real-PostgreSQL Accounting failure/retry, cancellation, event, concurrency, isolation, immutable-ledger, and outer-transaction regressions plus authenticated financial lifecycle and responsive accessibility checks.
- Extended the shared PostgreSQL transaction boundary to Inventory configuration, movements/batches, completion/corrections, and reservation lifecycle writes, including numbering, audit, and all nine existing event contracts while retaining advisory locks and immutable completed ledger entries.
- Rechecked reservation release after acquiring its stock lock to prevent a stale competing release from overwriting the winner or adding duplicate release evidence; same-key replays retain their existing result.
- Normalized completion replay keys consistently with their persisted values, preventing false concurrency errors for accepted keys with surrounding whitespace.
- Fixed Inventory product/location filters using incompatible component input-event binding, retained selected values while filtering, and reset correction-page busy state so its new draft actions remain usable without a reload.
- Added real-PostgreSQL Inventory rollback/retry, lock/concurrency, idempotency, event/ledger, sequence, isolation, and composition coverage plus browser workflows and responsive movement/balance/reservation/warehouse/location tables.
- Extended coordinated PostgreSQL transactions to Purchasing creation, editing, transitions, and archive/reactivate, including numbering, lines, audit, and its three existing event contracts without schema or permission changes.
- Batched Purchasing product lookups within the 100-ID directory limit while preserving the 500-line limit, and explicitly staged replacement lines to prevent false draft-edit concurrency failures.
- Added Purchasing rollback/retry, cancellation, sequence, event, concurrency, isolation, and outer-transaction regressions plus browser lifecycle and responsive table coverage.
- Extended coordinated PostgreSQL transactions to quotation/order writes, document numbering, audit records, and the existing Sales outbox events without schema, permission, or event-contract changes.
- Batched Sales product lookups sequentially within the directory's 100-ID limit while retaining the 500-line quotation limit.
- Fixed false quotation-edit concurrency errors by explicitly adding replacement line entities, and contained Sales detail tables in named keyboard-accessible scroll regions.
- Added real-PostgreSQL Sales rollback/retry, event-payload, sequence, line-replacement, conversion-race, and company-isolation regressions, plus quotation-to-order browser workflows.
- Extended shared PostgreSQL transactions to all CRM mutations: leads, opportunity conversion, pipeline stages, assignments, opportunity transitions, and activity completion/reopening, including audit flushes and explicitly staged outbox messages without schema or event-contract changes.
- Added real-PostgreSQL CRM rollback/retry and conversion regressions, shared-session registration/model checks, and browser coverage for CRM configuration and sales-pipeline workflows.
- Fixed company-user lookup and ordering queries to filter/sort before DTO projection, restoring PostgreSQL-backed CRM assignment without changing tenant/company membership checks.
- Updated browser regressions for the drawer's dynamic accessible labels, keyboard wrapping, scrim/Escape closure, and focus restoration; navigation checks wait for each new circuit before interacting.
- Replaced Product/CRM navigation-event waits with URL assertions followed by rendered-state checks, avoiding a missed-event timeout after a successful save without increasing timeouts or relaxing validation.
- Extended the shared transaction boundary to Products create/edit/archive/reactivate and their audit records, preserving ordinary CSV partial success and existing conflict/concurrency responses without schema changes.
- Made product-editor initialization sequential for the shared session and added controlled rendered-form, PostgreSQL failure/retry, and browser product lifecycle regressions.
- Added a shared PostgreSQL transaction coordinator and atomic Contacts business/audit writes, with rollback, nested-scope, cancellation, and retry regressions against real PostgreSQL.
- Made integration tests fail closed when Docker or container startup is unavailable; removed the external Accounting test-database override and centralized disposable container startup.
- Separated CI unit, architecture, integration, and browser gates and retained test-result and dashboard screenshot diagnostics on a pinned Ubuntu runner.
- Replaced the unavailable MinIO test-image reference with a disposable source-pinned BuildKit build using digest-pinned official Docker CLI, Go, and Alpine images, without weakening the S3 storage contract test; updated test-only Testcontainers to 4.16.
- Added a bounded HTTP-readiness gate and allowlisted browser setup-phase/login-status diagnostics, without changing authentication or workflow assertions or echoing raw test payloads in public annotations.
- Fixed first-run organization bootstrap to use the newly tracked company when seeding its base currency, with PostgreSQL regressions for fresh startup, restart idempotence, and existing-company currency backfill.
- Added a validated loopback API address for server-rendered pages and configured Docker Compose to use the internal port, fixing Inventory and Reporting when the browser uses a different published port.
- Fixed Organization mobile overflow by containing positioned table headers within their scroll area, retaining accessible labels and horizontal table scrolling.
- Documented the first transaction-adoption milestone, remaining release gates, and a community upgrade article without claiming production readiness.

- Added tenant/company security, roles, auditing, attachments, background jobs, and transactional outbox foundations.
- Added Contacts, Products, CRM, Sales, Purchasing, Inventory, Accounting, and Reporting modules.
- Added trusted external-module packaging, signatures, CLI, administrative lifecycle UI, and dependency-ordered migrations.
- Added a separately packaged `community.hello` reference module with CI validation of package, install, disable, and enable operations.
- Added responsive grouped navigation, queue recovery, production security headers, deployment hardening, and browser workflow coverage.
- Added Inventory source-document search and selectors for Purchase Order receipts, Sales Order deliveries, and reservation filtering without raw identifier entry.
- Added PostgreSQL Accounting workflow acceptance coverage for posting, allocation, reversal, concurrency, idempotency, closed periods, audit, and outbox behavior.
- Added real-PostgreSQL reporting reconciliation coverage across company-scoped Sales, Purchasing, financial statements, aging, and cash data, including server-translatable aggregate queries.
- Added a least-privilege PostgreSQL runtime grant script for migration/runtime identity separation.
- Adopted the Apache License 2.0 for the platform, built-in modules, and public package metadata.
- Reworked the product master list, details, and editor with company-aware currency and unit selectors, actionable field validation, responsive data layouts, and polished shared form and button styling.
- Added company creation and editing, active-company switching, membership-based session validation, and organization user administration with atomic access updates.
- Added Getting started and About pages plus confirmed, audited retention cleanup for completed company queue history.
- Fixed literal error text in CRM lead, sales quotation, and purchase order create/edit forms, with rendered-form regression coverage.
- Added a locally bundled Bootstrap interface, refreshed responsive operations layout, and permission-aware company report dashboards with accessible chart data alternatives.

This remains a pre-release contract. Production adoption and third-party module compatibility are not guaranteed until the 0.1.0 release is published.
