# Purchasing module

Purchasing owns tenant/company-scoped purchase orders and lines in the `purchasing` schema. Supplier identity, tax number, address, product description/SKU/unit, currency, exchange rate, prices, discounts, taxes, and totals are captured as historical document facts.

Suppliers and purchasing product facts are resolved only through public Contacts and Products contracts. The server calculates all totals with decimal arithmetic and issues `PO-{yyyy}-######` document numbers through the platform sequence service.

Draft orders are editable. They may be sent, confirmed, or cancelled; sent orders may be confirmed or cancelled. Confirmation publishes `purchasing.purchase_order.confirmed.v1` and `purchasing.goods_receipt.requested.v1`; cancellation publishes `purchasing.purchase_order.cancelled.v1`. Partially received and received states are reserved for Inventory integration. Optimistic concurrency, audit history, archive restrictions, search, sorting, paging, API authorization, and Blazor workflows are included.

As with Sales, the module context and platform outbox currently commit through separate EF contexts, so strict cross-context atomicity remains future infrastructure work.
