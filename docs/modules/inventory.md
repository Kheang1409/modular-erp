# Inventory module

Inventory owns warehouses, internal stock locations, explicit supplier/customer/inventory-gain/inventory-loss locations, immutable completed stock movements, and reservations in the `inventory` PostgreSQL schema. On-hand quantity is derived from completed movement ledger entries at internal locations; products do not contain a mutable stock field.

Receipts, deliveries, transfers, and adjustments begin as drafts. Receipts flow from Supplier or InventoryGain to Internal; deliveries flow from Internal to Customer or InventoryLoss; transfers flow Internal to Internal; and adjustments use InventoryGain to Internal or Internal to InventoryLoss. Purchase-order receipts require a Supplier origin and sales-order deliveries require a Customer destination.

Completion revalidates the current location types, is optimistic-concurrency protected and idempotent, and internal-source completion plus reservation uses PostgreSQL advisory transaction locks. Reservation creation also locks each tenant/company idempotency key: simultaneous identical requests return the same reservation, while reuse with a different payload is rejected. Negative internal stock is forbidden: stock cannot be delivered, transferred, adjusted out, or reserved beyond available quantity. External locations are movement boundaries rather than stock-holding locations. Corrections create an exact compensating movement rather than editing history.

The module reads product, sales-order, and purchase-order state through their public contract assemblies. It never reads or writes another module's schema. Events are published for reservations and completed receipts, shipments, transfers, and adjustments.

API routes are under `/api/v1/inventory`; Swagger documents warehouses, locations, bounded source-document lookup, searchable location/warehouse balance summaries, moves, atomic multi-line movement batches, and reservations. Source-document lookup uses the Sales and Purchasing public directory contracts and returns only confirmed, company-scoped documents with their eligible products. Batch creation validates every line before persisting any draft, while the immutable ledger continues to store one movement per product line. The Blazor pages support balance reconciliation, movement listing, type-aware draft creation, searchable Purchase Order and Sales Order selectors, completion/correction, detail, warehouse/location administration, source-document fulfillment, and reservation lifecycle visibility. Lot/serial tracking, costing, picking waves, and CSV operations are future extensions.

## Audit consistency

Inventory explicitly enlists its context through `IBusinessTransactionCoordinator`
and the scoped relational session. Warehouse/location creation and editing,
single/batch movement creation, draft cancellation, completion, compensating
correction creation, and reservation creation/release commit with their applicable
audit and outbox records. Delivery consumption of reservations belongs to the same
transaction as the completed movement and its shipment/fulfillment messages.

The boundary begins before database reads, number allocation, or staged changes.
A failed batch audit flush, partial event staging, cancellation, or late validation
rolls back its ledger/configuration/reservation changes, allocated numbers, and
platform records together. Return DTOs are constructed before commit. Rollback
clears participating trackers so an uncancelled retry can use the same service
scope. Successful idempotent replays complete their nested scope without adding
another audit/event or accidentally rolling back an outer transaction.

Existing Read Committed isolation and advisory lock keys/order are preserved.
Stock, document/product, reservation-key, and correction locks remain held until
the outer transaction completes. Reservation release rechecks the current record
after acquiring the stock lock: a same-key replay returns the existing release,
while a different-key stale writer cannot overwrite it or emit a duplicate release.
Completion retries compare the same trimmed idempotency key that is persisted, so
repeating an accepted padded key returns the original completion even with its
original concurrency stamp, without another ledger, audit, or outbox change.
Completed movements remain immutable; a correction creates a separately numbered
draft compensating movement that must itself be completed.

Product and source-order facts remain behind public contracts. Reads on the shared
connection must be sequential. The nine existing Inventory event names/payloads,
permissions, schema, quantity rules, and location matrix are unchanged. Fulfillment
messages report movement progress; this change does not introduce an automatic
Sales/Purchasing state-update consumer. Outbox delivery remains at least once and
does not make consumer effects atomic with the ledger or guarantee legal gapless
numbering. Accounting also enlists its own workflows; this does not create a new
automatic stock-costing or Inventory-to-ledger posting integration.

Movement, balance, reservation, warehouse, and location tables use named keyboard-focusable scroll
regions to keep populated wide tables contained on mobile. Navigating from a
completed movement to its draft correction resets the detail page's busy/error
state, keeping the new correction's Complete/Cancel actions available.
Product/location text filters use native input-event binding; typing must not
dispatch `ChangeEventArgs` to a string-only component callback and break the page.
Filtering retains selected products and locations, so visible selections continue
to match the values that the form submits. Dynamic options have stable identities
so filtering and clearing do not replace the selected browser option.
