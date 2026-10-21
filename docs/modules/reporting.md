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

General ledger and movement history expose row totals, Previous/Next navigation,
and 25/50/100 rows per page. Applying filters or changing page size starts at page
one. Editing a date alone does not change the displayed report or its CSV: run the
report to apply the new dates. Paging keeps the last applied dates. Tables
have captions and named, keyboard-focusable horizontal scroll regions on small
screens; loading, request failure, and empty results are separate states.

## Permissions

Each report family has a separate read permission. CSV downloads additionally require `reporting.export`. Grant only the report families a role needs.

## HTTP API

JSON endpoints are rooted at `/api/v1/reports`. Add `.csv` to the report endpoint for a spreadsheet-compatible export. CSV values use invariant formatting, quote every field, and prefix formula-like values to prevent spreadsheet formula injection.

Exports use UTF-8 without a byte-order mark and CRLF record separators. Output is
buffered and flushed per 200-row batch/page, not per row. The first query is
validated before CSV response headers are written; invalid date arguments return
structured HTTP 400. A later query failure terminates the export rather than
appending JSON to a partial CSV. Cancellation stops further page queries.

Paged exports retain the existing 200-row query contract, including its count on
each page. They are sequential live reads, not a repeatable-read snapshot: changes
made during an export can affect subsequent pages. Generate reconciliation
exports during a stable reporting window when a consistent source is required.

Examples:

```text
GET /api/v1/reports/sales/summary?from=2026-01-01&to=2026-01-31
GET /api/v1/reports/inventory/movements?from=2026-01-01&to=2026-01-31&page=1&pageSize=100
GET /api/v1/reports/financial/trial-balance.csv?asOf=2026-01-31
```

## Reconciliation

Financial statements and the general ledger include posted entries only. The
trial balance is derived from journal lines and should always reconcile total
debits to total credits. General ledger pages sort by business date, entry number,
account code, line sequence, and line ID; movement pages sort by business date,
number, and ID. Oversized page offsets return an empty page with the actual total,
without integer overflow. Inventory valuation is intentionally omitted until a
costing policy is introduced; on-hand quantities do not imply monetary valuation.

Receivables/payables aging includes non-draft, non-cancelled documents dated on or
before the selected date, including documents that have since been fully paid.
Open amounts are reconstructed from their allocations, not today's cached paid
balance. An original posted/reversed payment reduces the balance on its payment
date; its posted compensating journal adds that allocation back on the reversal's
business date. Draft and unallocated cash do not settle a document. Parties and
currencies remain separate, and each aging report aggregates in one read query.

This is business-date history, not an "as known at the time" audit snapshot. The
existing services permit backdated payments and reversals; their actual dates
remain authoritative. A reversal dated before its original payment can therefore
temporarily produce an open amount above the document's face value. This upgrade
does not impose a new date-ordering policy or change existing write behavior.
The cash summary retains its existing current-posted-payment-state semantics;
it is not a historical reversal cash-flow statement. Financial statement account
classification still uses the current chart of accounts.

Integration acceptance runs these aggregations against PostgreSQL and reconciles
company-isolated Sales and Purchasing totals, trial balance, general ledger,
profit and loss, balance sheet, aging, and posted cash payments. Dedicated
regressions cover dated allocation/reversal histories, every aging bucket edge,
currency and tenant/company isolation, calendar extremes, cancellation, and
multi-page detail ordering. Browser workflows compare populated tables, totals,
filters, and CSV output on desktop and phone-sized layouts. These bounded fixtures
are not a production-scale performance certification.

No report is cached by default. Any future caching must include tenant, company, permissions, filters, and source freshness in its key and invalidation design.
