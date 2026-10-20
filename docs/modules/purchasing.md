# Purchasing module

Purchasing owns tenant/company-scoped purchase orders and lines in the `purchasing` schema. Supplier identity, tax number, address, product description/SKU/unit, currency, exchange rate, prices, discounts, taxes, and totals are captured as historical document facts.

Suppliers and purchasing product facts are resolved only through public Contacts and Products contracts. The server calculates all totals with decimal arithmetic and issues `PO-{yyyy}-######` document numbers through the platform sequence service.

Draft orders are editable. They may be sent, confirmed, or cancelled; sent orders may be confirmed or cancelled. Confirmation publishes `purchasing.purchase_order.confirmed.v1` and `purchasing.goods_receipt.requested.v1`; cancellation publishes `purchasing.purchase_order.cancelled.v1`. Partially received and received states are reserved for Inventory integration. Optimistic concurrency, audit history, archive restrictions, search, sorting, paging, API authorization, and Blazor workflows are included.

## Audit consistency

Purchasing uses the shared scoped relational session and explicitly enlists through
`IBusinessTransactionCoordinator`. Creation, draft editing, transitions, and
archive/reactivate commit purchase-order headers, lines, audit records, and any
applicable outbox messages in one PostgreSQL transaction.

The boundary starts before document-number allocation. A failure after allocation,
line replacement, or an audit/outbox flush rolls back those changes together.
Return DTOs are built before commit. Rollback clears participating trackers so an
uncancelled retry can use the same service scope, while optimistic concurrency
continues to reject stale edits. Replacement lines are explicitly inserted as new
rows rather than inferred to be updates because their IDs are already assigned.

Supplier and product reads remain behind public directories. Product references
are resolved sequentially in batches of at most 100 IDs, preserving the existing
500-line limit without exceeding the Products directory contract. The shared
connection must not be used concurrently. Explicit outer transactions can compose
Contacts, Products, and Purchasing writes without an inner service committing the
caller's transaction.

The three existing event names and payloads, permissions, schema, state rules, and
historical commercial snapshots are unchanged. Confirmation stages both the order
confirmation and goods-receipt-request messages; it does not itself create an
Inventory receipt. Outbox delivery retains its existing semantics, not an
exactly-once delivery or legal gapless-numbering guarantee. Inventory and Accounting
also enlist their own workflows; Accounting reads source-order snapshots through
public contracts, without making downstream consumer effects atomic with an order.

Purchase-order list and detail tables use named, keyboard-focusable scroll regions
so wide rows scroll locally on mobile rather than widening the page.
