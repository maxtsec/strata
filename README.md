# Strata

A multi-tenant document collaboration platform built on .NET, ASP.NET Core, and
Azure — a portfolio project demonstrating enterprise architecture and a
security-first engineering mindset.

The name comes from the Australian *strata title*: one building, many
separately owned lots, shared structure underneath. That's multi-tenancy in
physical form — many customer organisations on one deployment, each fully
isolated from the others.

## Status: Phase 0 — Walking Skeleton (in progress)

This project is being built in phases, each ending in a working, deployed
increment rather than a pile of untested code. Currently on Phase 0: an empty
API with the right architecture, deployed and running on Azure before any
business logic exists.

- [x] Solution structure: four projects, dependencies enforced in one direction
- [ ] `/health` endpoint
- [ ] Deployed to Azure App Service
- [ ] CI/CD via GitHub Actions
- [ ] Azure SQL provisioned, connection string in Key Vault

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
| `Strata.Application` | Use cases. Declares the interfaces it needs (`IDocumentRepository`, `IFileStorage`, ...) without implementing them. |
| `Strata.Infrastructure` | Implements the interfaces `Application` declares: EF Core, Azure Blob Storage, email. |
| `Strata.Api` | Controllers, request validation, and dependency injection wiring (the only place `Infrastructure` is referenced directly). |

Architecture decisions are recorded as they're made in `docs/adr/`.

## Roadmap

1. **Walking skeleton** — empty API, deployed, CI/CD green *(current)*
2. **Single-tenant core** — users, documents, folders, auth, file upload/download, roles, share links
3. **Multi-tenant retrofit** — tenant resolution, EF Core global query filters, a `SaveChanges` interceptor to close the gap query filters leave on writes, then an attempt to break the isolation between two tenants
4. **Notification subsystem** — pluggable channel abstraction (email, in-app), background dispatch, retry with backoff, idempotency
5. **Production hardening** — structured logging, global exception handling, health checks, rate limiting
6. **Self-pentest** — Burp Suite against tenant isolation, SAS token abuse, upload attack surface, share-link entropy, IDOR; findings written up and fixed

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
```

Run and endpoint instructions will be added once `/health` exists.
