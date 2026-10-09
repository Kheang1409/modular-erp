# Changelog

All notable changes are documented here. The project follows semantic versioning for platform and public module contracts.

## 0.1.0 - unreleased

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
