# Database migrations

Platform migrations live under `Erp.Infrastructure/Persistence/Migrations`. Create a migration only after reviewing the model change:

```powershell
dotnet ef migrations add Name --project src/Platform/Erp.Infrastructure --startup-project src/Platform/Erp.Host --context PlatformDbContext --output-dir Persistence/Migrations
```

Inspect generated SQL before production deployment. Never silently ignore a failed migration or use `EnsureCreated` in the application host.
