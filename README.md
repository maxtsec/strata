# Strata

A multi-tenant document collaboration platform built on .NET, ASP.NET Core, and
Azure — a portfolio project demonstrating enterprise architecture and a
security-first engineering mindset.

The name comes from the Australian *strata title*: one building, many
separately owned lots, shared structure underneath. That's multi-tenancy in
physical form — many customer organisations on one deployment, each fully
isolated from the others.

## Status: Phase 1 in progress

This project is being built in phases, each ending in a working, deployed
increment rather than a pile of untested code.

**Phase 0 — Walking skeleton** *(complete)*
- [x] Solution structure: four projects, dependencies enforced in one direction (and covered by architecture tests)
- [x] `/health` endpoint
- [x] Deployed to Azure App Service
- [x] CI/CD via GitHub Actions (OIDC — no long-lived Azure credential stored in GitHub)
- [x] Azure SQL provisioned, connection string in Key Vault (App Service reads it via its managed identity)

**Phase 1 — Single-tenant core** *(current)*
- [x] `Document` / `Folder` / `DocumentShare` domain entities
- [x] ASP.NET Core Identity wired (Guid-keyed `ApplicationUser`, so it lines up with the entities' owner/user references with no string↔Guid conversion)
- [x] EF Core migrations (applied to local dev DB; not yet applied to Azure SQL)
- [x] Auth: `POST /api/auth/register` and `/login`, issuing JWTs
- [x] File upload/download via Blob Storage — user-delegation SAS URIs, no static storage key anywhere
- [ ] Folders CRUD
- [ ] Roles / share links (the `DocumentShare` entity exists; no endpoints use it yet)
- [ ] Apply the migration to Azure SQL

## Architecture

Clean Architecture / Dependency Inversion: business rules know nothing about
databases or cloud services; the technical details depend on the business
rules, not the other way round.

```
Strata.Api  ──────────────┐
    │                     │
    ▼                     ▼
Strata.Application   Strata.Infrastructure
    │                     │
    ▼                     │
Strata.Domain  ◄──────────┘
```

| Project | Responsibility |
|---|---|
| `Strata.Domain` | Entities, value objects, and business rules. No dependencies on EF Core, Azure SDKs, or ASP.NET — it should be unit-testable with nothing running. |
| `Strata.Application` | Use cases. Depends on EF Core only for the `DbSet<T>` type, via a thin `IApplicationDbContext` interface it declares itself (see [ADR 0001](docs/adr/0001-no-repository-abstraction.md)) — no repository layer, no provider-specific code. |
| `Strata.Infrastructure` | Implements `IApplicationDbContext` (`AppDbContext`, EF Core, migrations) and `IFileStorage` (Azure Blob Storage via SAS). Also hosts ASP.NET Core Identity's `ApplicationUser`. |
| `Strata.Api` | Controllers, request validation, and dependency injection wiring (the only place `Infrastructure` is referenced directly). |

The dependency direction itself is enforced by `tests/Strata.Architecture.Tests`,
not just documented here. Architecture decisions are recorded as they're made
in `docs/adr/`.

## Roadmap

0. **Walking skeleton** — empty API, deployed, CI/CD green *(complete)*
1. **Single-tenant core** *(current)* — users, documents, folders, auth, file upload/download, roles, share links
2. **Multi-tenant retrofit** — tenant resolution, EF Core global query filters, a `SaveChanges` interceptor to close the gap query filters leave on writes, then integration tests in CI that try to break the isolation between two tenants
3. **Notification subsystem** — pluggable channel abstraction (email, in-app), background dispatch, retry with backoff, idempotency
4. **Production hardening** — structured logging, global exception handling, health checks, rate limiting
5. **Multi-tenant RAG** — semantic search and Q&A over each tenant's own documents; the interesting problem is that tenant isolation has to hold in the vector layer too, a weaker, easier-to-leak system than the relational one
6. **Packaging** — architecture diagram, screenshots, ADRs tidied, spoken walkthroughs

Tenant isolation is proven by integration tests running in CI (Phase 2), not
by a manual security audit — there's no separate pentest phase. Security here
means isolation enforced in code and continuously verified, the same way the
architecture tests already enforce the dependency graph in Phase 0.

## Tech stack

.NET (latest LTS) · ASP.NET Core Web API · EF Core · Azure SQL · ASP.NET Core
Identity + JWT · Azure Blob Storage (SAS tokens) · Serilog + Application
Insights · xUnit · Azure App Service · GitHub Actions

## Development approach

Built through structured pair-programming with Claude Code, under an explicit
learning contract: concepts are explained before any code is written, and the
architecturally significant code is written by hand rather than generated —
the goal is to be able to defend every decision in an interview, not just to
have a finished repo.

## Running locally

Prerequisites, once per machine:

- [Git](https://git-scm.com/downloads/win)
- [.NET SDK](https://learn.microsoft.com/dotnet/core/install/windows) matching
  `global.json` (currently .NET 10, 10.0.4xx feature band)
- [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/)
  using its WSL 2 backend and Linux containers
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli-windows)

### First-time setup on Windows

Open PowerShell, clone the repository, and start the local SQL Server:

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

`Strata_Dev_2026!` is a known local/test-only credential. The port is bound to
loopback only; never reuse this password for Azure or another real environment.
The Docker volume preserves the database between restarts.

Restore tools and packages, then create the local configuration:

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

`az login` lets `DefaultAzureCredential` access Blob Storage locally; deployed
code uses managed identity instead. On macOS/Linux, use the same commands in a
Bash-compatible shell and replace PowerShell's backtick continuations with `\`.

Then, each session:

```powershell
docker start strata-sql
dotnet build
dotnet test
dotnet run --project src/Strata.Api
```

`/health` is then available at the URL `dotnet run` prints (e.g.
`http://localhost:5115/health`).

`dotnet test` also runs `tests/Strata.Api.IntegrationTests` — real HTTP
requests through `WebApplicationFactory` against a second, dedicated
database (`StrataIntegrationTests`) on the same SQL Server container, reset
between tests with [Respawn](https://github.com/jbogard/Respawn). No extra
setup needed beyond the container already being up; the connection string
defaults to the local/test credential above. Override it with the
`STRATA_TEST_CONNECTION_STRING` environment variable if your container uses a
different password; CI points it at its own SQL Server service container.
