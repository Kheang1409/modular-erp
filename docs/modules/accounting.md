# Accounting module

Accounting owns the chart of accounts, fiscal periods, journals, general ledger, customer invoices, supplier bills, payments, allocations, tax codes, dated exchange rates, and bank reconciliations in the `accounting` PostgreSQL schema. All records are tenant- and company-scoped.

## Posting controls

Journal entries require at least two one-sided lines and equal debit and credit totals. Base-currency amounts are calculated on the server from the retained transaction-currency rate. Posting is idempotent, requires an open fiscal period, and turns the entry immutable. Corrections use linked compensating reversals.

Closing a fiscal period is concurrency protected and fails while it contains draft journal entries, customer invoices, supplier bills, or payments. Historical references may remain inactive without invalidating existing entries.

## Receivables and payables

Customer invoices snapshot confirmed or fulfilled Sales orders through `Sales.Contracts`. Supplier bills snapshot confirmed or received purchase orders through `Purchasing.Contracts`; supplier invoice references are unique per supplier. The Accounting module never reads another module's schema directly.

Invoice and bill totals, tax, currency, exchange rate, party identity, and source lines are copied from the server-side source document. Posting creates the ledger entry transactionally. Draft documents may be cancelled; posted documents require a reversal or credit workflow.

Customer receipts and supplier payments support partial allocations and unapplied balances. Allocations must reference open documents for the same party and currency, cannot exceed either the document balance or payment amount, and update document status transactionally. Reversing a posted payment restores the affected open balances.

## Financial controls

Tax codes retain rates and optional input/output accounts. Effective-dated exchange rates are append-only and resolved using the latest rate on or before a business date. Posted transactions always retain their explicit currency and exchange-rate snapshot.

Bank reconciliations match posted journal entries containing movement in the chosen asset account. A journal entry can be cleared only once, and a reconciliation can be finalized only when its statement difference is zero.

## Integration and security

Permissions distinguish read, configuration, journal posting, period close, invoices, bills, payments, and reconciliation. Every API endpoint declares a permission. Material state changes append audit records. Posted entries, invoices, bills, payments, reversals, and period closes publish registered integration events through the platform outbox.

The interactive pages are available under the Accounting navigation group. API routes use `/api/v1/accounting`.
