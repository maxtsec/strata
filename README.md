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

- .NET SDK matching `global.json` (currently pinned to the 10.0.4xx feature band)
- Docker (Desktop on Windows/Mac), running a local SQL Server 2022 container — same setup on every machine, and the same engine family as Azure SQL, so migrations behave identically locally and deployed:
  ```bash
  docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=<pick a password>" \
    -p 1433:1433 --name strata-sql --hostname strata-sql \
    -v strata-sql-data:/var/opt/mssql \
    -d mcr.microsoft.com/mssql/server:2022-latest
  ```
  This is local-only — kept separate from the Azure SQL instance on purpose. The volume persists data across container restarts; `docker start strata-sql` brings it back after a reboot. (On Windows PowerShell, either drop the `\` line continuations and run it as one line, or swap them for `` ` ``.)
- `az login`, so `DefaultAzureCredential` can reach Blob Storage locally the same way the deployed app does via its managed identity
- `dotnet tool restore` — installs `dotnet-ef` at the version pinned in `.config/dotnet-tools.json`
- Two local secrets, set once (use the same SA password as the `docker run` command above):
  ```bash
  dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost,1433;Database=Strata;User Id=sa;Password=<same password>;TrustServerCertificate=True;MultipleActiveResultSets=true" --project src/Strata.Api
  dotnet user-secrets set "Jwt:SigningKey" "<any random 32+ byte value, base64 is fine>" --project src/Strata.Api
  ```
- `dotnet ef database update --project src/Strata.Infrastructure --startup-project src/Strata.Api` — creates the local database and applies migrations

Then, each session:

```bash
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
defaults to the same `sa` credentials as local dev and can be overridden with
the `STRATA_TEST_CONNECTION_STRING` environment variable (CI sets this to
point at its own service container).
