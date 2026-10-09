# Currency configuration

Currency configuration belongs to the platform because every financial module needs the same company-scoped currency context. `ICurrencyConfigurationService` lets modules consume it without depending on EF Core or platform tables.

Each company has one base currency code and an explicit set of active currencies. Codes are normalized to three uppercase ASCII letters, monetary values use `decimal`, and minor units are constrained to zero through six. Currency edits carry a concurrency stamp; stale edits fail rather than overwrite a newer configuration. The base-currency operation also requires the caller's previously observed base code.

Exchange rates are company and tenant scoped, positive `decimal(28,12)` values, and effective-dated. A currency pair can have only one rate on a given date. Rates are append-only through the public service so later changes do not rewrite historical rates; corrections should add a rate with the appropriate effective date. Financial modules must persist the rate actually used when posting a transaction and must not recalculate posted history from today's configuration.

The protected API is rooted at `/api/v1/platform/currencies` and requires `platform.currency.manage`. Changes append audit entries. Bootstrap creates the configured base currency, while the migration backfills it for existing companies.

ISO-style syntax validation does not claim that every three-letter code is an officially assigned ISO 4217 currency. A maintained reference-data catalog and rate-provider integrations remain future work.
