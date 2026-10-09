# Sales module

Sales owns quotations and quotation lines in the `sales` PostgreSQL schema. Records are tenant/company scoped, use optimistic concurrency, and capture customer name, product SKU/description/unit, currency, base currency, and the effective exchange rate for historical validity.

Sales references customers and products only through `IContactDirectory` and `IProductDirectory`. It never reads Contacts or Products tables. Customers must be active customer contacts; products must be active. Product prices are defaults only and server-side calculation always determines subtotals, discounts, taxes, and totals using decimal arithmetic.

Quotation numbers use the platform sequence service. Draft quotations may be edited and sent. Sent quotations may be accepted or rejected. Draft or sent quotations may be cancelled, while expiration requires a past valid-until date. Active drafts and sent quotations cannot be archived. Important changes are audited; create, send, and accept operations publish versioned integration events to the platform outbox.

Permissions are `sales.quotation.read`, `sales.quotation.manage`, and `sales.quotation.confirm`. APIs are rooted at `/api/v1/sales/quotations`; the Blazor UI includes search, editable lines, totals, details, and workflow actions.

Accepted quotations can be converted exactly once into sales orders. Conversion allocates an `SO-{yyyy}-` sequence number and snapshots customer identity, tax number, billing/shipping addresses, product descriptions, units, prices, discounts, taxes, currency, exchange rate, and totals. Draft orders can be confirmed or cancelled; confirmed orders can be cancelled until fulfillment integration begins. Dedicated order permissions protect reading, conversion, confirmation, and cancellation. Confirmation and cancellation emit `sales.order.confirmed.v1` and `sales.order.cancelled.v1`.

The Sales context and platform outbox currently commit through separate EF Core contexts. Events are durable once the audit/outbox flush completes, but a process failure between the Sales commit and platform flush can leave a quotation without its event. A shared transaction coordinator or module-owned outbox is required for strict atomicity.
