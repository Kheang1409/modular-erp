# Transactional outbox

Integration events that must survive process failure are written through `IOutboxWriter`. The writer adds a versioned JSON message to the caller's scoped `PlatformDbContext` but deliberately does not call `SaveChanges`. Platform-owned changes and those messages can share its transaction. A separate module context is atomic with platform audit/outbox writes only when it explicitly enlists through the [business transaction coordinator](overview.md#business-transaction-boundary); merely sharing a database name is not enough. Contacts, Products, CRM, Sales, Purchasing, Inventory, and Accounting are integrated business modules; other writable services and future modules still require explicit adoption. This adoption does not introduce new Contacts, Products, or CRM integration event types and preserves the five existing versioned Sales contracts, three Purchasing contracts, nine Inventory contracts, and six Accounting contracts.

Modules register immutable event payloads through `IIntegrationEventRegistry` using stable lowercase names such as `sample.greeting.published.v1`. A CLR payload type and external event name can each be registered only once, preventing ambiguous deserialization or accidental version aliases. Consumers implement `IIntegrationEventConsumer<TEvent>` and remain dependent only on public abstractions.

The hosted publisher atomically claims messages using `FOR UPDATE SKIP LOCKED`, establishes the stored tenant/company context, and invokes scoped consumers sequentially. Messages retain correlation, retry, bounded failure, processed, and dead-letter metadata. Failed delivery uses exponential retry delays capped at five minutes; exhausted messages remain available for diagnosis.

Every claim stores a fresh GUID `LeaseId`. Success/failure updates must still own
that ID on the same tenant/company message, with neither processed nor dead-letter
state set. Accepted results clear the token and lease deadline. A superseded
attempt updates nothing and emits a lease-lost diagnostic instead of claiming
that its completion/failure was saved. Retry counts and deadlines are not ownership
tokens: replay resets counts, and clocks can repeat. The existing strict deadline
comparison is retained; expiration permits reclamation, while a still-owning
attempt may finish until another claim or replay invalidates it.
This change does not add lease renewal or consumer execution timeouts.

Dead-letter replay conditionally resets only an eligible row and clears any old
ownership token. The reset and audit flush share the business transaction
coordinator, including outer-transaction composition. A competing request that
finds no eligible row returns false without a replay audit. A later, genuinely new
dead-letter transition can be replayed again; there is no new replay-idempotency
key or per-consumer checkpoint contract. Permissions and payload-free metadata
remain unchanged.

The nullable lease column preserves legacy records without inventing ownership
for old attempts. Follow the [queue upgrade procedure](../deployment/production.md#queue-lease-ownership-upgrade)
before running new workers; old binaries do not enforce this fence.

Delivery is at least once. A process may stop after a consumer's side effect but before `ProcessedAtUtc` is saved, so consumers must use the event ID as an idempotency key where side effects matter. Authorized operators can inspect payload-free dead-letter metadata and request an audited replay from **Administration > Queue recovery**. The current publisher targets in-process consumers. External transports, automatic retention cleanup, and per-consumer delivery checkpoints remain future adapters.
