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

## CSV behavior

Import accepts UTF-8 CSV up to 5 MB and 10,000 data rows. Required columns are `type`, `sku`, and `name`; price, currency, unit, and inventory columns are validated by the same application service used by the API. Import is intentionally partial: valid rows are committed and every rejected row is returned with its line number. Callers must inspect the result rather than assuming the whole file succeeded.

Export streams pages of 100 records, uses invariant decimal formatting, quotes every field, escapes quotes, and prefixes spreadsheet formula-leading values with an apostrophe.

## Permissions and routes

Permissions are `products.read`, `products.manage`, `products.archive`, `products.import`, and `products.export`.

The `/api/v1/products` API provides paginated search, details, create, update, archive, reactivate, CSV import, and streaming CSV export. The `/products` Blazor area provides search/filtering, pagination, product/service create and edit forms, details, price/currency display, and archive/reactivate actions.
