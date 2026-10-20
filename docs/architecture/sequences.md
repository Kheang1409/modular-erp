# Document sequences

`ISequenceService` allocates human-readable, company-scoped document numbers such as `SO-2026-000001`. Callers provide a stable lowercase key, a prefix template, numeric padding, and an explicit business date. Prefixes support `{yyyy}`, `{MM}`, and `{dd}` tokens.

Definitions are unique by tenant, company, and key. PostgreSQL creates a missing definition with `INSERT ... ON CONFLICT DO NOTHING`, then locks and advances its counter atomically with `UPDATE ... RETURNING` in the same transaction. This avoids the unsafe `MAX(number) + 1` pattern. Concurrent requests may receive numbers in a different completion order, but cannot receive duplicates.

The first call fixes a key's prefix template and padding; later calls with conflicting configuration are rejected. Padding and positive-counter invariants also have database check constraints. Counters do not currently reset by fiscal period.

Allocation joins an active platform transaction, including an explicitly enlisted
[business transaction](overview.md#business-transaction-boundary). Sales,
Purchasing, and Inventory begin that boundary before allocation, so rollback restores the
definition/counter along with the document. Without an active transaction, the
allocator commits its own short transaction; a later caller failure cannot reclaim
that allocation. This is not a legal gapless-numbering guarantee. Modules needing
legally controlled numbering require a dedicated posting-time policy rather than
reusing this general allocator.

Accounting retains its existing date/GUID-based `JE`, `RV`, `INV`, `BILL`, `PAY`,
and `REC` formats. Its shared transaction adoption does not switch those documents
to this allocator or introduce a posting-time or legal gapless-numbering policy.
