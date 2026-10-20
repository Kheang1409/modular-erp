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

## Audit consistency

All twenty Accounting write methods explicitly enlist their context through
`IBusinessTransactionCoordinator` and the scoped relational session. Account,
period, and journal configuration, journal creation/posting/reversal, invoice and
bill creation/posting/cancellation, payment creation/posting/reversal, tax codes,
dated currency rates, and reconciliation creation/finalization commit with their
applicable platform audit and existing outbox records.

The boundary starts before database reads or staged mutations. In particular,
payment allocation changes, invoice/bill paid state, the posted or compensating
entry and lines, and audit/events have one durable outcome. Failures or cancellation
roll them back together and clear participating trackers for a same-scope retry.
Return DTOs are resolved before commit. Successful replay/no-op paths complete
their nested scope without accidentally marking a caller's outer transaction
rollback-only; inner services do not commit that outer transaction.

This preserves existing Read Committed isolation, optimistic concurrency, scoped
unique constraints, six event contracts, permissions, money rounding, exchange-rate
and commercial snapshots, and posted-ledger immutability. No migration, new advisory
lock, or change to existing key/state-transition policies is introduced. Existing
date/GUID-based document numbers are retained; Accounting does not use the platform
sequence allocator. This is not a legal gapless-numbering guarantee.

Existing open-period checks are not a new serialization guarantee against a
simultaneous period close, and competing same-key creations/reversals may still be
rejected by uniqueness constraints. Outbox delivery remains at least once; consumer
side effects and external systems are not part of the database transaction.
Sales/Purchasing source facts stay behind their public contracts, and reads on the
shared connection must be sequential.

## Posting screens

Invoice, bill, and payment screens require explicit journal/account selections;
they do not infer receivables, bank, tax, or payable roles from account-list order.
Selections use active direct-posting accounts and the existing account-type choices.
Payment bank and control accounts must differ; tax accounts are required for taxed
documents. Server-side posting validation and authorization remain authoritative.

Journal-entry line controls have accessible labels. Populated Accounting tables
use named, keyboard-focusable scroll regions, captions, and column headings so
wide rows remain contained on phone-sized screens.
Ledger list and individual reads use the same nullable reversal links: an
unreversed posted entry has no reversal ID, keeping its Reverse action available.
