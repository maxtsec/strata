# Strata — Technical Document

> Companion documents: [Design Document](design.md) ·
> [設計文檔（中文）](design.zh.md) · [技術文檔（中文）](technical.zh.md)

This document covers how Strata is built, run, tested, and deployed. For *why*
it is shaped this way, see the [Design Document](design.md).

## 1. Tech stack

| Area | Choice |
|---|---|
| Runtime | .NET 10 (pinned by `global.json`, 10.0.4xx feature band) |
| API | ASP.NET Core Web API, controller-based |
| Data | EF Core + Azure SQL (SQL Server 2022 locally, in Docker) |
| Identity | ASP.NET Core Identity (`AddIdentityCore`) + JWT bearer (HMAC-SHA256) |
| Files | Azure Blob Storage, user-delegation SAS URIs |
| Tests | xUnit, NetArchTest (layering), Respawn (database reset) |
| CI/CD | GitHub Actions → Azure App Service (OIDC, no stored credential) |
| Secrets | Azure Key Vault (deployed) / user-secrets (local) |

## 2. Repository layout

```
strata/
├── Strata.slnx
├── global.json
├── AGENTS.md / CLAUDE.md      # working agreement for AI-assisted development
├── README.md
├── .github/workflows/deploy.yml
├── docs/
│   ├── design.md / design.zh.md
│   ├── technical.md / technical.zh.md
│   └── adr/                   # 0001–0004
├── src/
│   ├── Strata.Domain/         # entities, ITenantOwned, IOwnable
│   ├── Strata.Application/    # IApplicationDbContext, IFileStorage, ICurrentTenant
│   ├── Strata.Infrastructure/ # AppDbContext, migrations, Blob Storage, Identity user
│   └── Strata.Api/            # controllers, DI wiring, tenancy + authorization
└── tests/
    ├── Strata.Architecture.Tests/
    └── Strata.Api.IntegrationTests/
```

Namespaces follow folders: `Strata.Domain.Documents`,
`Strata.Infrastructure.Persistence`, `Strata.Api.Controllers`.

## 3. Domain model

```
Tenant 1 ──── * ApplicationUser
   │                 │
   │ (TenantId)      │ (OwnerId)
   ▼                 ▼
Folder * ────────► Document * ────────► DocumentShare
   ▲ ParentFolderId    (FolderId)          (DocumentId, UserId)
   └── self-reference
```

| Entity | Key fields | Notes |
|---|---|---|
| `Tenant` | `Id`, `Name` (≤200, required), `CreatedAt` | Not tenant-filtered |
| `ApplicationUser` | `IdentityUser<Guid>` + `TenantId` | `Guid`-keyed; not tenant-filtered |
| `Folder` | `Id`, `OwnerId`, `TenantId`, `ParentFolderId?`, `Name` | Self-referencing hierarchy |
| `Document` | `Id`, `OwnerId`, `TenantId`, `FolderId?`, `Name`, `Size`, `ContentType` | Bytes live in Blob Storage, keyed by `Id` |
| `DocumentShare` | `Id`, `DocumentId`, `UserId`, `TenantId`, `UserRole` | `UserRole` ∈ {`Member`, `Viewer`} |

Two marker interfaces in `Strata.Domain` drive cross-cutting behaviour:

- `IOwnable` (`Guid OwnerId`) — consumed by `OwnerAuthorizationHandler`.
- `ITenantOwned` (`Guid TenantId`) — consumed by the write interceptor.

`Id`, `OwnerId`, and `TenantId` are `init`-only. Note that `init` only blocks
direct C# assignment on an already-constructed instance — it does **not** stop
`Update(new Document { ... })` from attaching a detached entity whose
`TenantId` says anything at all, which is exactly why the write interceptor
also validates `Modified` entries (see §6.4).

### Relational configuration

All foreign keys use `DeleteBehavior.Restrict` except
`DocumentShare → Document`, which cascades (deleting a document removes its
shares).

| Table | Indexes |
|---|---|
| `Folders` | `TenantId` |
| `Documents` | `TenantId` |
| `DocumentShares` | `TenantId`; unique `(DocumentId, UserId)` |
| `AspNetUsers` | `TenantId` |

The unique `(DocumentId, UserId)` index is the real guard against duplicate
shares; the application's pre-check is an optimisation, and `CreateShare`
catches `DbUpdateException` and *re-verifies* rather than assuming the
constraint was the cause — an unrelated failure must not be reported as
"already shared".

## 4. Migration history

Applied in order; every migration is reviewed by hand before being applied and
is never auto-applied by CI.

| Migration | Contents |
|---|---|
| `20260902113900_InitialCreate` | Identity tables, `Folders`, `Documents` |
| `20260904102714_AddDocumentSharesUniqueIndex` | Unique `(DocumentId, UserId)` |
| `20260905020127_AddTenant` | `Tenants` table only |
| `20260905025451_AddApplicationUserTenantId` | Required `ApplicationUser.TenantId`, legacy users backfilled to a deterministic Legacy Tenant |
| `20260905051133_AddTenantIdToResources` | Required `TenantId` on `Folders` / `Documents` / `DocumentShares` |

### The staged, fail-closed migration pattern

`dotnet ef migrations add` generates `NOT NULL DEFAULT Guid.Empty` when adding
a required column to a table with existing rows. That default is silently
wrong: it invents a tenant. Both tenant migrations were rewritten by hand into
five stages:

1. Add the column as **nullable**.
2. **Backfill from real relationships**, not a sentinel —
   `Folders`/`Documents` from `OwnerId → AspNetUsers.TenantId`;
   `DocumentShares` from `DocumentId → Documents.TenantId` (a share follows
   its *document*, never its recipient, so a pre-existing cross-tenant
   recipient is preserved rather than relabelled).
3. **Validate, failing closed** — `RAISERROR` aborts the migration if any row
   is still null, or if a child folder disagrees with its parent, or a
   document with its folder.
4. `ALTER COLUMN ... NOT NULL`.
5. Add indexes and foreign keys. No permanent default constraint is left
   behind anywhere.

The `AddTenantIdToResources` migration was rehearsed against a disposable
database migrated to N−1 and seeded with representative cross-tenant data,
including a deliberate negative test proving the `RAISERROR` checks actually
abort and leave zero partial changes.

## 5. Request pipeline and DI

`Program.cs`, in order: controllers → health checks → OpenAPI →
`TenantWriteGuardInterceptor` (scoped) → `AppDbContext` (with the interceptor
attached) → `IApplicationDbContext` → Identity core → JWT bearer →
`JwtTokenGenerator` → `IHttpContextAccessor` → `ICurrentTenant` →
`IFileStorage` → authorization handlers.

| Service | Lifetime | Why |
|---|---|---|
| `AppDbContext` / `IApplicationDbContext` | Scoped | Per request |
| `TenantWriteGuardInterceptor` | Scoped | Depends on scoped `ICurrentTenant` |
| `ICurrentTenant` → `HttpContextCurrentTenant` | Scoped | Per-request tenant identity |
| `IFileStorage` → `BlobFileStorage` | Singleton | Stateless; holds no per-request state |
| `OwnerAuthorizationHandler` | Singleton | Pure check against the passed resource, no I/O |
| `DocumentAccess` / `DocumentEdit` handlers | Scoped | Query `DocumentShares`, so they need the DbContext |

`AddDbContext` uses the `(sp, options)` overload specifically so the
interceptor can be resolved from the request scope — the single-argument
overload has no `IServiceProvider` and cannot reach a scoped `ICurrentTenant`.

Pipeline: `UseHttpsRedirection` → `UseAuthentication` → `UseAuthorization` →
controllers + `/health`.

## 6. Key mechanisms

### 6.1 Tenant claim validation — `Program.cs` + `TenantClaimTypes`

`TenantClaimTypes.TryGetValidTenantId` (in `Strata.Application.Tenancy`, so
the issuer and both enforcement points share one definition) requires
**exactly one** `tenant_id` claim, parseable as a `Guid`, and not
`Guid.Empty`. It never takes the first of several.

It runs in the JWT bearer `OnTokenValidated` event, so a token failing that
check is rejected with 401 at the authentication boundary and never reaches a
controller. `MapInboundClaims = false` keeps claim types unmangled (`sub`
stays `sub`).

### 6.2 `ICurrentTenant` — `Strata.Api/Tenancy/HttpContextCurrentTenant.cs`

```csharp
public bool IsAvailable => _httpContextAccessor.HttpContext is not null;
public Guid TenantId { get; }   // resolves on each access; throws if invalid
```

Resolution is **lazy** — the constructor stores only the accessor. This is
what allows `AppDbContext` (which takes an `ICurrentTenant`) to be constructed
outside an HTTP request, as migrations and test setup require. Fail-closed
behaviour is preserved, just moved to the moment tenant identity is actually
read.

`IsAvailable` answers "is there a request at all", **not** "is the claim
valid" — a request with a bad claim reports `true` and then throws on
`TenantId`, because that case should be unreachable past §6.1 and must not be
quietly skipped.

### 6.3 Global query filters — `AppDbContext.OnModelCreating`

```csharp
builder.Entity<Folder>().HasQueryFilter(f => f.TenantId == _currentTenant.TenantId);
builder.Entity<Document>().HasQueryFilter(d => d.TenantId == _currentTenant.TenantId);
builder.Entity<DocumentShare>().HasQueryFilter(s => s.TenantId == _currentTenant.TenantId);
```

Each filter closes over `_currentTenant` (the injected service, not a copied
`Guid`), so EF Core re-evaluates it against the live `DbContext` instance on
every query rather than baking a value in at model-build time.

`Tenant` and `ApplicationUser` are intentionally unfiltered.

Consequence for lookups: `FindAsync` is **not** used for tenant-owned
resources anywhere, because it can return an already-tracked entity without
querying — and therefore without the filter applying. All lookups are
`SingleOrDefaultAsync(x => x.Id == id, ct)`.

### 6.4 Write interceptor — `TenantWriteGuardInterceptor`

Implements `SaveChangesInterceptor`, overriding both `SavingChanges` and
`SavingChangesAsync`. For each `ITenantOwned` entry in state `Added`,
`Modified`, or `Deleted`:

1. If `!_currentTenant.IsAvailable` → throw. A tenant-owned write with no
   trusted tenant is refused, never treated as an implicit admin bypass.
2. If `Deleted` or `Modified` → read the row's **real** current `TenantId` via
   `EntityEntry.GetDatabaseValuesAsync()` and compare that to the current
   tenant. `GetDatabaseValues` deliberately ignores query filters (it exists
   to report true database state), so a foreign row comes back with its real
   `TenantId` and is rejected by the explicit comparison. `null` means the row
   genuinely does not exist.
3. If `Added` or `Modified` → also compare the **value being written**. This
   is what stops an owned row being retargeted into another tenant.

It validates only; it never assigns a `TenantId`.

Migrations never reach this code — schema DDL and `migrationBuilder.Sql`
backfills do not go through `SaveChanges` or the change tracker.

### 6.5 Authorization handlers — `Strata.Api/Authorization/`

| Requirement | Handler | Rule |
|---|---|---|
| `OwnerRequirement` | `OwnerAuthorizationHandler` | `sub` claim == `resource.OwnerId`. Pure, no I/O. |
| `DocumentAccessRequirement` | `DocumentAccessAuthorizationHandler` | Owner, **or** any `DocumentShare` for this user |
| `DocumentEditRequirement` | `DocumentEditAuthorizationHandler` | Owner, **or** a share with `Role.Member` |

The two share-aware handlers query `DocumentShares` through the same
`IApplicationDbContext`, so those queries inherit the tenant filter
automatically — no separate tenant logic was needed in them.

## 7. API surface

| Method | Route | Auth | Notes |
|---|---|---|---|
| `POST` | `/api/auth/register` | anonymous | Creates a `Tenant` **and** its first user atomically; returns a JWT |
| `POST` | `/api/auth/login` | anonymous | Returns a JWT |
| `GET` | `/api/folders` | JWT | Caller's own folders |
| `POST` | `/api/folders` | JWT | `TenantId` stamped server-side from `ICurrentTenant` |
| `PUT` | `/api/folders/{id}` | JWT, owner | Rename / re-parent, with cycle detection |
| `DELETE` | `/api/folders/{id}` | JWT, owner | 409 if not empty |
| `POST` | `/api/documents` | JWT | Returns `documentId` + a 15-minute upload SAS URI |
| `GET` | `/api/documents/{id}/download` | JWT, owner or share | Returns a 15-minute download SAS URI |
| `PUT` | `/api/documents/{id}` | JWT, owner or `Member` | Rename |
| `POST` | `/api/documents/{id}/shares` | JWT, owner | 409 on duplicate |
| `GET` | `/api/documents/{id}/shares` | JWT, owner | |
| `DELETE` | `/api/documents/{id}/shares/{shareId}` | JWT, owner | |
| `GET` | `/health` | anonymous | |

Registration is atomic without an explicit transaction: the `Tenant` is
tracked as `Added` first, and Identity's own `SaveChangesAsync` — which runs
only after all its validation passes — flushes both in one transaction.

Every create path assigns `TenantId` from `ICurrentTenant` server-side; a
client-supplied `tenantId` field is inert (covered by tests). A share is
stamped with its **document's** tenant, never the recipient's.

Missing and unauthorised resources both return 404 (anti-enumeration).

## 8. Testing

89 tests: 2 architecture + 87 integration.

### Architecture tests (`Strata.Architecture.Tests`)

NetArchTest asserts `Strata.Domain` has no dependency on `Strata.Application`,
`Strata.Infrastructure`, `Strata.Api`, EF Core, or ASP.NET Core; and that
`Strata.Application` depends on neither `Strata.Infrastructure` nor
`Strata.Api`. The layering is enforced, not just documented.

### Integration tests (`Strata.Api.IntegrationTests`)

Real HTTP requests through `WebApplicationFactory<Program>` against a real
SQL Server (Docker locally, a service container in CI) — no in-memory
provider, so unique indexes, FK behaviour, and real SQL semantics are actually
exercised.

| Component | Role |
|---|---|
| `IntegrationTestFixture` | One migrated database per collection; Respawn resets table contents before every test |
| `StrataWebApplicationFactory` | Hosts the real app; swaps `IFileStorage` for `FakeFileStorage` |
| `FakeFileStorage` | Counts upload/download calls so tests can assert Blob Storage was **not** reached |
| `TestApiHelpers` | Register/authenticate, create folders/documents/shares, hand-craft JWTs |
| `FixedCurrentTenant` / `NoCurrentTenant` | Test doubles for `ICurrentTenant` |

Two fixture escape hatches, used deliberately:

- `QueryDbAsync(...)` — administrative assertions against a fresh scoped
  `AppDbContext`. Every such read on a tenant-owned set calls
  `IgnoreQueryFilters()` explicitly, so a filter bypass in a test is always
  visible at the call site. It has no `HttpContext`, so any tenant-owned
  *write* through it fails closed.
- `CreateDbContext(ICurrentTenant)` — builds an `AppDbContext` with the real
  interceptor and a caller-supplied tenant identity, bypassing DI/HTTP. This
  is how tests act as a specific tenant, and the only way to exercise the
  interceptor's throw paths.

Notable test groups:

- `TenantClaimAuthenticationTests` — missing / malformed / empty / duplicate
  `tenant_id` claims all yield 401, via real HTTP with really-signed tokens.
- `HttpContextCurrentTenantTests` — lazy resolution, fail-closed access,
  construction never throwing, `IsAvailable` semantics.
- `TenantReadIsolationTests` — adversarial rows whose `OwnerId` is the acting
  user's own but whose `TenantId` is a different real tenant, so owner
  authorization *would* allow them. Covers list, folder update/delete,
  document create-in-folder / download, and share create/list/delete, each
  asserting no database mutation and no Blob Storage call.
- `TenantWriteIsolationTests` — the interceptor directly: foreign `TenantId`
  on add; stub delete with the row's real tenant; stub delete with a **forged**
  current-tenant id; `Update` retargeting A→B; `Update` on a foreign row with a
  forged id; no-tenant-context writes; plus the legitimate counterparts, and
  one test covering the synchronous `SaveChanges` path.
- Sharing behaviour — same-tenant Member/Viewer tests seed a sibling user
  directly via `UserManager`, because every `/api/auth/register` call mints a
  brand-new tenant and therefore cannot produce two users in one tenant.

## 9. Running locally (Windows)

Prerequisites: Git, .NET SDK matching `global.json`, Docker Desktop (WSL 2,
Linux containers), Azure CLI.

```powershell
git clone https://github.com/maxtsec/strata.git
Set-Location strata

docker run --name strata-sql --hostname strata-sql `
  -e "ACCEPT_EULA=Y" `
  -e "MSSQL_SA_PASSWORD=Strata_Dev_2026!" `
  -p 127.0.0.1:1433:1433 `
  -v strata-sql-data:/var/opt/mssql `
  -d mcr.microsoft.com/mssql/server:2022-latest
```

`Strata_Dev_2026!` is a known local/test-only credential; the port is bound to
loopback only, and it must never be reused for Azure or any real environment.

```powershell
dotnet tool restore
dotnet restore

$jwtBytes = New-Object byte[] 48
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($jwtBytes)
$jwtKey = [Convert]::ToBase64String($jwtBytes)
$rng.Dispose()

dotnet user-secrets set 'ConnectionStrings:DefaultConnection' 'Server=tcp:127.0.0.1,1433;Database=Strata;User Id=sa;Password=Strata_Dev_2026!;TrustServerCertificate=True;MultipleActiveResultSets=true' --project src/Strata.Api
dotnet user-secrets set 'Jwt:SigningKey' $jwtKey --project src/Strata.Api
az login
dotnet ef database update --project src/Strata.Infrastructure --startup-project src/Strata.Api
```

Use `127.0.0.1` rather than `localhost` — on Windows, `localhost` can resolve
to IPv6 and fail to reach the container's IPv4 port binding.

`az login` lets `DefaultAzureCredential` reach Blob Storage locally; deployed
code uses the App Service's managed identity instead.

Each session:

```powershell
docker start strata-sql
dotnet build
dotnet test
dotnet run --project src/Strata.Api
```

`dotnet test` uses a second database (`StrataIntegrationTests`) on the same
container. Override the connection with `STRATA_TEST_CONNECTION_STRING` if the
local password differs.

## 10. Azure resources and deployment

| Resource | Name | Notes |
|---|---|---|
| Resource group | `rg-strata-dev` | |
| App Service | `strata-api` | Linux, .NET 10; `httpsOnly` enabled |
| SQL Server / DB | `sql-strata-dev` / `strata` | Entra ID admin; no static SQL login used by the app |
| Key Vault | — | Holds the connection string; App Service reads it via managed identity |
| Blob Storage | — | User-delegation SAS; app identity needs Storage Blob Data Contributor |

### CI/CD

`.github/workflows/deploy.yml`:

- **build-and-test** — on push to `main` and on every PR. Restores, builds
  Release, runs the full test suite against a SQL Server 2022 service
  container.
- **deploy** — on push to `main` only, after tests pass. `dotnet publish`,
  then `azure/login@v2` via **OIDC** (`id-token: write`; no long-lived Azure
  credential is stored in GitHub) and `azure/webapps-deploy@v3`.

Migrations are **never** applied by CI.

Git workflow: one feature branch per logical unit of work → PR → CI green →
**rebase and merge** → delete the branch. Never commit straight to `main`.

### Key Vault references — a real gotcha

An App Service Key Vault reference that pins a secret **version**
(`@Microsoft.KeyVault(SecretUri=https://…/secrets/Name/<version>)`) never
picks up newly-set versions. Omit the version segment so the reference always
resolves to the current one. This cost real debugging time and is worth
knowing before it does so again.

### Applying a migration to Azure SQL

From a developer machine, on demand. Azure SQL authenticates the same way Blob
Storage does — an Entra ID identity via `az login`, no static SQL login.

```powershell
az sql server ad-admin list --server sql-strata-dev --resource-group rg-strata-dev
```

Applying needs the current IP allowed through the server firewall for the
duration of the command — and nothing left open afterwards, including when the
migration itself fails. `try`/`finally` guarantees the rule is removed either
way:

```powershell
$myIp = (Invoke-RestMethod -Uri "https://api.ipify.org?format=json").ip
az sql server firewall-rule create --server sql-strata-dev --resource-group rg-strata-dev `
  --name "dev-temp" --start-ip-address $myIp --end-ip-address $myIp

try {
  dotnet ef database update --project src/Strata.Infrastructure --startup-project src/Strata.Api `
    --connection "Server=tcp:sql-strata-dev.database.windows.net,1433;Database=strata;Authentication=Active Directory Default;TrustServerCertificate=False;Encrypt=True;"

  dotnet ef migrations list --project src/Strata.Infrastructure --startup-project src/Strata.Api `
    --connection "Server=tcp:sql-strata-dev.database.windows.net,1433;Database=strata;Authentication=Active Directory Default;TrustServerCertificate=False;Encrypt=True;"
}
finally {
  az sql server firewall-rule delete --server sql-strata-dev --resource-group rg-strata-dev --name "dev-temp"
}
```

`Authentication=Active Directory Default` resolves the same credential chain
as `DefaultAzureCredential`. This is separate from how the deployed app
connects at runtime (that connection string stays in Key Vault).

### Controlled maintenance deployments

When a new schema and the old application are incompatible — for example
adding `NOT NULL TenantId` columns the running app does not populate — the
order matters:

1. Stop the App Service; verify it is actually stopped.
2. Apply the reviewed migration from the exact reviewed commit.
3. Verify the schema read-only: migration row present once, columns `NOT NULL`,
   no default constraints, no null/`Guid.Empty` values, relationships
   consistent, indexes and foreign keys present.
4. Only then merge and let the deployment run.
5. Start the app, check `/health` and startup logs, re-query migration history.

Stopping first eliminates the window in which the old application would be
running against the new schema — it cannot insert rows lacking the now-required
column, so the failure mode is removed rather than raced.

## 11. Conventions

- Async everywhere; `CancellationToken` on anything crossing a boundary.
- No secrets in source or `appsettings` — Key Vault, or user-secrets locally.
- Every migration reviewed before applying; never auto-applied in CI.
- Structured logging (Serilog), never `Console.WriteLine`.
- Conventional commits.
- `Strata.Domain` must not reference EF Core, Azure SDKs, or ASP.NET — not
  even a `using`. The test of whether the layering is real: business rules
  should be unit-testable with nothing running.
