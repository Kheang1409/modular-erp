# Document sequences

`ISequenceService` allocates human-readable, company-scoped document numbers such as `SO-2026-000001`. Callers provide a stable lowercase key, a prefix template, numeric padding, and an explicit business date. Prefixes support `{yyyy}`, `{MM}`, and `{dd}` tokens.

Definitions are unique by tenant, company, and key. PostgreSQL creates a missing definition with `INSERT ... ON CONFLICT DO NOTHING`, then locks and advances its counter atomically with `UPDATE ... RETURNING` in the same transaction. This avoids the unsafe `MAX(number) + 1` pattern. Concurrent requests may receive numbers in a different completion order, but cannot receive duplicates.

The first call fixes a key's prefix template and padding; later calls with conflicting configuration are rejected. Padding and positive-counter invariants also have database check constraints. Counters do not currently reset by fiscal period, and allocated numbers are not reclaimed after a caller's later business transaction fails. Modules needing gapless legally controlled numbering require a dedicated posting-time policy rather than reusing this general allocator.
