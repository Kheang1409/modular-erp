# In-process domain events

`IDomainEvent` marks immutable facts that occurred inside one ERP process. Modules subscribe with normal scoped `IDomainEventConsumer<TEvent>` registrations and publish through `IDomainEventDispatcher`; they do not reference infrastructure or locate handlers through global state.

The dispatcher resolves every typed handler from the caller's dependency-injection scope and invokes them sequentially in registration order. It supplies the active tenant, company, authenticated user when present, and request correlation identifier. Sequential dispatch makes ordering explicit and allows handlers to share an enclosing application transaction. Cancellation and handler exceptions propagate to the publisher; later handlers are not run after a failure.

The SampleModule demonstrates the complete contract with `SampleGreetingRequested` and its handler. Its protected demonstration endpoint is `POST /api/v1/sample/events/greeting-requested`.

These events are intentionally in-process and provide no durability after a transaction commits. Anything that must survive process failure or reach another system must be recorded through the transactional outbox. Event handlers should avoid slow external I/O and should be safe when the publishing use case is retried.
