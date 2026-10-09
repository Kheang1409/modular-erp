# Reporting module

The Reporting module is a read-only composition layer over Sales, Purchasing, Inventory, and Accounting. It does not own transactional data or duplicate another module's business rules.

## Reports

The visual dashboard is available at `/reports/dashboard`. It uses the same
company-scoped report endpoints as the detail pages. Users see only the report
families permitted by their existing roles; `reporting.dashboard` is a derived
authorization policy, not a permission that needs to be assigned to a role.

Choose the business-date range and apply the filter before interpreting the
results. Monetary operational charts use the selected chart currency and never
combine currencies. Financial statements retain the company's base currency.
Stock quantities are kept separate by unit of measure, and stock valuation is
not inferred from quantities. Historical on-hand stock uses the chosen as-of
date, while reservation figures reflect currently active reservations.

Charts include a readable data-table alternative and honest no-data states.
Failed report requests are identified separately from successful empty reports.
Existing detail tables and permission-protected CSV exports remain available
for reconciliation. All chart assets are served by the application itself.

- Sales summary, sales by customer, and sales by product
- Purchase summary
- Inventory on hand and paged movement history
- Trial balance, general ledger, profit and loss, and balance sheet
- Receivables and payables aging
- Cash summary

Every request requires an explicit date range or as-of date and is scoped by the current tenant and company. Responses include a UTC generation timestamp. Large detail exports are fetched and written one page at a time.

## Permissions

Each report family has a separate read permission. CSV downloads additionally require `reporting.export`. Grant only the report families a role needs.

## HTTP API

JSON endpoints are rooted at `/api/v1/reports`. Add `.csv` to the report endpoint for a spreadsheet-compatible export. CSV values use invariant formatting, quote every field, and prefix formula-like values to prevent spreadsheet formula injection.

Examples:

```text
GET /api/v1/reports/sales/summary?from=2026-01-01&to=2026-01-31
GET /api/v1/reports/inventory/movements?from=2026-01-01&to=2026-01-31&page=1&pageSize=100
GET /api/v1/reports/financial/trial-balance.csv?asOf=2026-01-31
```

## Reconciliation

Financial reports include posted entries only. The trial balance is derived from journal lines and should always reconcile total debits to total credits. Aging uses the open amount at the requested as-of date. Inventory valuation is intentionally omitted until a costing policy is introduced; on-hand quantities do not imply monetary valuation.

Integration acceptance runs these aggregations against PostgreSQL and reconciles company-isolated Sales and Purchasing totals, trial balance, general ledger, profit and loss, balance sheet, receivables and payables aging, and posted cash movements. Draft, cancelled, reversed, and other-company records are excluded by the same persisted queries used in production.

No report is cached by default. Any future caching must include tenant, company, permissions, filters, and source freshness in its key and invalidation design.
