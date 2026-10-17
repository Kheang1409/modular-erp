# Products module

The Products module owns the sellable product and service catalog. It has its own `products` PostgreSQL schema, EF Core context, migrations, API, Blazor pages, permissions, and public lookup contract.

## Boundaries

- Products depends on platform abstractions and `Products.Contracts` only.
- Sales, Purchasing, and other consuming modules resolve catalog references through public product directory contracts; they must not read `ProductsDbContext` or product tables.
- Inventory quantity is deliberately absent from the catalog. Stock transactions and balance projections belong to the Inventory module.
- Currency validity is checked through `ICurrencyConfigurationService`, not platform persistence.
- Archived products remain resolvable through the public directory for historical documents.

## Data rules

- SKU and non-empty barcode uniqueness are scoped to tenant and company.
- Barcode comparison currently uses PostgreSQL's case-sensitive text semantics after trimming; no global barcode registry is assumed.
- Prices and costs are non-negative `decimal(18,4)` values and always carry an active company currency code.
- Services cannot track inventory.
- Sales and purchase units must reference an active unit of measure. The initial stable codes are `EA`, `HUR`, `KGM`, and `MTR`; conversion rules are intentionally deferred.
- The concurrency stamp must be supplied for update, archive, and reactivate operations. Stale writes return HTTP 409.

## Audit consistency

Create, update, archive, and reactivate operations enlist the Products context
with platform audit persistence through `IBusinessTransactionCoordinator`. The
product, audit entry, and any explicitly staged platform outbox messages commit
or roll back together. A failed update restores the persisted product and its
concurrency stamp; rollback also clears participating change trackers before a
later operation can retry. This does not add new Product event types.

The module uses the scoped relational session without depending on platform
entities. Product and company-option reads must be sequential, including when
loading the edit form. Explicit outer transactions can compose Products and
Contacts operations; inner completion never commits the outer transaction.

## CSV behavior

Import accepts UTF-8 CSV up to 5 MB and 10,000 data rows. Required columns are `type`, `sku`, and `name`; price, currency, unit, and inventory columns are validated by the same application service used by the API. Import is intentionally partial: valid rows are committed and every rejected row is returned with its line number. Callers must inspect the result rather than assuming the whole file succeeded.

Export streams pages of 100 records, uses invariant decimal formatting, quotes every field, escapes quotes, and prefixes spreadsheet formula-leading values with an apostrophe.

Ordinary imports commit each successful row and its audit independently. When a
caller deliberately wraps an import in an outer business transaction, nested
rollback-only rules apply instead; it is not an independent per-row commit.

## Permissions and routes

Permissions are `products.read`, `products.manage`, `products.archive`, `products.import`, and `products.export`.

The `/api/v1/products` API provides paginated search, details, create, update, archive, reactivate, CSV import, and streaming CSV export. The `/products` Blazor area provides search/filtering, pagination, product/service create and edit forms, details, price/currency display, and archive/reactivate actions.
