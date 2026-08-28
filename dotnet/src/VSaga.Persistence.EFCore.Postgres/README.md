# VSaga.Persistence.EFCore.Postgres

PostgreSQL provider wiring for `VSaga.Persistence.EFCore` — migrations and connection setup for running
vSaga sagas against Postgres. Kept as a separate package from `VSaga.Persistence.EFCore` specifically so
that package stays provider-agnostic.

## Install

```bash
dotnet add package VSaga.Persistence.EFCore.Postgres
```

## Usage

```csharp
services.AddVSagaEfCore(db => db.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly("VSaga.Persistence.EFCore.Postgres")));

// at startup:
using var scope = app.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<VSagaDbContext>();
await db.Database.MigrateAsync();
```

## Docs

[docs/persistence.md](https://github.com/RafaelJCamara/vSaga/blob/main/docs/persistence.md) has the full
migration history and the `MigrationsAssembly` requirement this package exists to satisfy.

## License

MIT
