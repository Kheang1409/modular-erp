# Manufacturing module

Manufacturing owns bills of materials, production work orders, status history,
and quality inspection records in the `manufacturing` schema. The API is under
`/api/v1/manufacturing`. `manufacturing.read` protects reads; bill of materials,
work-order creation/transitions, and quality inspections have separate
permissions.

Bill-of-material versions have draft, active, and retired states. A work order
captures its active BOM and planned quantity as a production snapshot, then
records explicit status transitions and inspection outcomes. Completing a work
order records actual output quantity, consumes the bill-of-material components,
and receives finished goods at the selected internal inventory location. These
stock movements are created and completed in the same coordinated database
transaction as the work-order completion, with `ManufacturingWorkOrder` source
references. Manufacturing provisions company-scoped Inventory Gain and Inventory
Loss locations if they do not already exist. Tenant/company filters, optimistic
concurrency, validation, and audit records protect changes. Releasing a work
order creates idempotent component reservations in Inventory; cancellation and
completion release remaining reservations transactionally. A company-level
daily labor-hour limit is checked against overlapping released and in-progress
orders before release.

Bill-of-material component lines can carry standard unit costs. Work orders
show estimated material cost and capture actual material, labor, and overhead
costs at completion, with a total production cost in the work order's chosen
currency. Actual material cost defaults to the BOM standard-cost estimate and
can be adjusted to reflect the company's inventory valuation. The module does
not yet post production cost journals to Accounting or derive actual material
cost automatically from Inventory valuation. Inventory's production contract
currently coordinates quantities only, and Accounting exposes no public
manufacturing posting command. Closing this gap requires explicit Inventory
valuation and Accounting posting contracts with an idempotent posting workflow;
direct cross-schema writes would bypass module ownership. Manufacturing uses
public Inventory contracts and does not read or write the Inventory schema
directly.
