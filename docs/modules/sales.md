# Sales module

Sales owns quotations and quotation lines in the `sales` PostgreSQL schema. Records are tenant/company scoped, use optimistic concurrency, and capture customer name, product SKU/description/unit, currency, base currency, and the effective exchange rate for historical validity.

Sales references customers and products only through `IContactDirectory`, `ICustomerDocumentDirectory`, and `IProductDirectory`. It never reads Contacts or Products tables. Customers must be active customer contacts; products must be active. Product prices are defaults only and server-side calculation always determines subtotals, discounts, taxes, and totals using decimal arithmetic.

Quotation numbers use the platform sequence service. Draft quotations may be edited and sent. Sent quotations may be accepted or rejected. Draft or sent quotations may be cancelled, while expiration requires a past valid-until date. Active drafts and sent quotations cannot be archived. Important changes are audited; create, send, and accept operations publish versioned integration events to the platform outbox.

Permissions are `sales.quotation.read`, `sales.quotation.manage`, and `sales.quotation.confirm`. APIs are rooted at `/api/v1/sales/quotations`; the Blazor UI includes search, editable lines, totals, details, and workflow actions.

Accepted quotations can be converted exactly once into sales orders. Conversion allocates an `SO-{yyyy}-` sequence number and snapshots customer identity, tax number, billing/shipping addresses, product descriptions, units, prices, discounts, taxes, currency, exchange rate, and totals. Draft orders can be confirmed or cancelled; confirmed orders can be cancelled until fulfillment integration begins. Dedicated order permissions protect reading, conversion, confirmation, and cancellation. Confirmation and cancellation emit `sales.order.confirmed.v1` and `sales.order.cancelled.v1`.

## Audit consistency

Sales uses the shared scoped relational session and explicitly enlists through
`IBusinessTransactionCoordinator`. Quotation creation, draft editing, transitions,
archive/reactivate, accepted-quotation conversion, and order confirmation/cancellation
commit their headers, lines, audit records, and applicable outbox messages together.
The five existing Sales event names and payloads are unchanged; publication retains
the platform outbox's existing delivery semantics.

The boundary starts before number allocation. A rolled-back operation restores its
sequence definition/counter as well as its document data and audit/outbox records.
Return DTOs are built before commit. Failed line replacement restores the original
rows and version, and rollback clears participating trackers so an uncancelled retry
can use the same service scope. Optimistic concurrency and the database's unique
source-quotation index still protect competing writers and duplicate conversion.
Replacement lines are explicitly staged as new rows; preassigned IDs must not make
EF treat them as updates to nonexistent rows.

Customer/product reads remain behind public directories. Product references are
resolved sequentially in batches of at most 100, allowing the existing maximum of
500 quotation lines without exceeding the Products directory contract. The shared
connection must not be used concurrently.

Explicit outer transactions can compose Contacts, Products, and Sales writes;
inner services do not commit the caller's transaction. This adoption changes no
schema, permissions, state-transition rules, or stored commercial snapshot formats.
Purchasing, Inventory, and Accounting also enlist their own workflows through the
shared transaction boundary. Accounting snapshots source orders through public
contracts; this does not make asynchronous consumer effects atomic with Sales.

Quotation and order detail tables use named, keyboard-focusable scroll regions,
so long document lines scroll locally rather than widening the mobile page.
