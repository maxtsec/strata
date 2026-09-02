# Strata

A multi-tenant document collaboration platform built on .NET, ASP.NET Core, and
Azure — a portfolio project demonstrating enterprise architecture and a
security-first engineering mindset.

The name comes from the Australian *strata title*: one building, many
separately owned lots, shared structure underneath. That's multi-tenancy in
physical form — many customer organisations on one deployment, each fully
isolated from the others.

## Status: Phase 0 complete — starting Phase 1

This project is being built in phases, each ending in a working, deployed
increment rather than a pile of untested code. Phase 0 proved the whole chain
— code, tests, deployment, secrets — works end to end before any business
logic exists.

- [x] Solution structure: four projects, dependencies enforced in one direction (and covered by architecture tests)
- [x] `/health` endpoint
- [x] Deployed to Azure App Service
- [x] CI/CD via GitHub Actions (OIDC — no long-lived Azure credential stored in GitHub)
- [x] Azure SQL provisioned, connection string in Key Vault (App Service reads it via its managed identity)

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
| `Strata.Infrastructure` | Implements `IApplicationDbContext` (`AppDbContext`, EF Core, migrations), plus Azure Blob Storage and email. |
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

```bash
dotnet build
dotnet test
dotnet run --project src/Strata.Api
```

`/health` is then available at the URL `dotnet run` prints (e.g.
`http://localhost:5115/health`).
