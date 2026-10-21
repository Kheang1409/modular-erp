# Currency configuration

Currency configuration belongs to the platform because every financial module needs the same company-scoped currency context. `ICurrencyConfigurationService` lets modules consume it without depending on EF Core or platform tables.

Each company has one base currency code and an explicit set of active currencies. Codes are normalized to three uppercase ASCII letters, monetary values use `decimal`, and minor units are constrained to zero through six. Currency edits carry a concurrency stamp; stale edits fail rather than overwrite a newer configuration. The base-currency operation also requires the caller's previously observed base code.

Exchange rates are company and tenant scoped, positive `decimal(28,12)` values, and effective-dated. A currency pair can have only one rate on a given date. Rates are append-only through the public service so later changes do not rewrite historical rates; corrections should add a rate with the appropriate effective date. Financial modules must persist the rate actually used when posting a transaction and must not recalculate posted history from today's configuration.

The protected API is rooted at `/api/v1/platform/currencies` and requires `platform.currency.manage`. Changes append audit entries. Bootstrap creates the configured base currency, while the migration backfills it for existing companies.

Configuration, base-currency changes, and exchange-rate creation use the shared
PostgreSQL transaction coordinator. Business changes and audit records commit or
roll back together, including when composed inside a caller's outer transaction.
No new currency integration events are introduced; explicitly staged outer
outbox work can participate in that same transaction.

Each writer locks the tenant/company row before reading currency state and holds
the lock until the owning transaction ends. A concurrent base change cannot race
past currency deactivation, and concurrent exchange-rate creation rechecks the
existing pair/date under that lock. Different companies use different rows.
Currency stamps and the expected-base-code check still reject stale requests;
these locks do not change rounding, effective-date rules, or posted history.
Reads remain ordinary sequential queries, not a new snapshot guarantee.

ISO-style syntax validation does not claim that every three-letter code is an officially assigned ISO 4217 currency. A maintained reference-data catalog and rate-provider integrations remain future work.
