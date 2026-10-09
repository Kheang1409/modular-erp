# Multi-company administration

The supported workspace model is one organization (tenant) with multiple companies in one PostgreSQL database. Company membership and the active company claim select the business data boundary. Tenant roles and module installation are shared; contacts, products, commercial documents, inventory, accounts, currencies, sequences, and reports remain company-scoped.

## Setup

1. Sign in as an administrator and open `/organization`.
2. Add a company with a unique organization-wide code, base currency, and time zone. The creator receives membership automatically and can select the new company in the header.
3. Create access profiles in `/administration/roles`, then open `/administration/users` to create users or update existing accounts. Select company access, a default company, and organization roles.
4. Switch to the intended company before setting up master data and daily workflows. Each company starts with separate business data and settings.

Company switches submit an authenticated, antiforgery-protected form and refresh the sign-in cookie. A valid active selection survives normal cookie refresh even if another session changes the user's persisted default. HTTP requests check the cookie's membership, active company and organization, lockout, and security stamp. Interactive sessions revalidate these every 30 seconds.

Role and company changes save in one transaction, reject stale selections, and revoke previous sessions when access changes. Users must retain an active company. The final enabled assignee of a system role is protected; self-edit guards preserve access to the current company and security administration. Company edits require the last observed version and reject deactivation that would strand an enabled user.

## Administrative surface

| Action | Endpoint | Permission |
| --- | --- | --- |
| Read current workspace | `GET /api/v1/platform/context` | `platform.organization.read` |
| List accessible companies | `GET /api/v1/platform/companies` | `platform.organization.read` |
| Update organization settings with expected version | `PUT /api/v1/platform/organization` | `platform.organization.manage` |
| Create company | `POST /api/v1/platform/companies` | `platform.organization.manage` |
| Update company with expected version | `PUT /api/v1/platform/companies/{id}` | `platform.organization.manage` |
| List or create users | `GET/POST /api/v1/platform/users` | `platform.security.manage` |
| Replace company and role access atomically | `PUT /api/v1/platform/roles/users/{id}/access` | `platform.security.manage` |
| Enable or disable user | `PUT /api/v1/platform/users/{id}/active` | `platform.security.manage` |
| Reset password | `POST /api/v1/platform/users/{id}/password` | `platform.security.manage` |
| Preview old completed work | `GET /api/v1/platform/operations/maintenance/preview?retentionDays=90` | `platform.operations.manage` |
| Clean approved completed work | `POST /api/v1/platform/operations/maintenance/execute` | `platform.operations.manage` |

Company lists show membership companies by default. Organization or security administrators can see the complete tenant directory. Business endpoint permissions remain independently enforced by their module policies.

## Safe data maintenance

The maintenance page at `/administration/data-maintenance` retains at least 30 days and at most 3,650 days of completed queue history. A protected preview binds the tenant, company, code, cutoff, exact eligible records, counts, and 30-minute expiry. Execution requires the preview token plus `CLEAN <company code>` typed exactly.

Only successful jobs without duplicate-prevention (idempotency) keys, locks, or errors and processed outbox messages without dead-letter state, locks, or errors are eligible. Pending work, failed work, jobs with idempotency keys, business data, and audits remain. Any scope or selection change requires a fresh preview. Deletes and the audit entry commit together; an audit failure rolls back cleanup.

This is retention of completed operational history, not a company or database reset. A full reset requires a separately reviewed backup and deployment operation.
