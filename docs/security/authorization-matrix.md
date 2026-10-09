# Authorization matrix

All API endpoints require authentication. Permissions are tenant-scoped role assignments. Navigation visibility is a convenience; server endpoint policies are the authorization boundary.

| Area | Read permissions | Write or privileged permissions |
| --- | --- | --- |
| Platform | `platform.organization.read`, `platform.audit.read`, `platform.attachment.read` | `platform.organization.manage`, `platform.security.manage`, `platform.currency.manage`, `platform.attachment.manage`, `platform.module.manage`, `platform.operations.manage` |
| Contacts | `contacts.read` | `contacts.manage`, `contacts.archive`, `contacts.import`, `contacts.export` |
| Products | `products.read` | `products.manage`, `products.archive`, `products.import`, `products.export` |
| CRM | `crm.read` | `crm.manage`, `crm.assign`, `crm.configure`, `crm.archive` |
| Sales | `sales.quotation.read`, `sales.order.read` | `sales.quotation.manage`, `sales.quotation.confirm`, `sales.order.manage`, `sales.order.confirm`, `sales.order.cancel` |
| Purchasing | `purchasing.order.read` | `purchasing.order.manage`, `purchasing.order.approve`, `purchasing.order.cancel`, `purchasing.order.archive` |
| Inventory | `inventory.read` | `inventory.configure`, `inventory.move.manage`, `inventory.move.complete`, `inventory.reserve`, `inventory.adjust` |
| Accounting | `accounting.read`, `accounting.invoice.read`, `accounting.bill.read`, `accounting.payment.read` | `accounting.configure`, `accounting.post`, `accounting.period.close`, `accounting.invoice.manage`, `accounting.invoice.post`, `accounting.bill.manage`, `accounting.bill.post`, `accounting.payment.manage`, `accounting.payment.post`, `accounting.reconcile` |
| Reporting | `reporting.sales`, `reporting.purchasing`, `reporting.inventory`, `reporting.financial`, `reporting.receivables`, `reporting.payables`, `reporting.cash` | `reporting.export` (combined with the relevant report permission) |

## Operational rules

- The system administrator role is synchronized with registered permissions at startup.
- Role changes use concurrency stamps and reject unknown permissions.
- Company and tenant query filters remain active underneath permission checks.
- Module-provided permissions must exactly match their manifest declaration.
- Privileged lifecycle, replay, posting, reversal, and role workflows produce audit records.
- New endpoints must use `RequirePermission`; architecture tests enforce authorization metadata.
- User access replacements save company memberships and roles atomically; cross-tenant identifiers and removal of the final active system administrator are rejected.
- Company switching requires an active membership and antiforgery verification. Cookie and interactive-session validation reject revoked or disabled access.
- Company maintenance previews and execution require `platform.operations.manage`, preserve business records and audit history, and bind cleanup to the reviewed company and record selection.
