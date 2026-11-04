# Database migrations

Platform migrations live under `Erp.Infrastructure/Persistence/Migrations`. Create a migration only after reviewing the model change:

```powershell
dotnet ef migrations add Name --project src/Platform/Erp.Infrastructure --startup-project src/Platform/Erp.Host --context PlatformDbContext --output-dir Persistence/Migrations
```

Inspect generated SQL before production deployment. Never silently ignore a failed migration or use `EnsureCreated` in the application host.

For large PostgreSQL tables, use the provider's concurrent index option when a
normal index build would block application writes. Concurrent index creation is
non-transactional; if it is interrupted before the migration is recorded, inspect
the index and migration history before retrying. The production recovery guidance
describes the specific fulfillment index procedure.
