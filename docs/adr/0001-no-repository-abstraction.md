# 0001. No repository abstraction over EF Core

Status: Accepted

## Decision

`Strata.Application` will not define per-aggregate repository interfaces
(`IDocumentRepository`, `IUserRepository`, ...). Instead, `Strata.Application`
declares a thin `IApplicationDbContext` interface exposing `DbSet<T>`
properties and `SaveChangesAsync`; `Strata.Infrastructure`'s `AppDbContext`
implements it. Use cases inject `IApplicationDbContext` and write LINQ
directly against the `DbSet<T>` properties, with `Include`, further `Where`/
`OrderBy` composition, and EF Core's native change tracking all working as
normal, because the object underneath really is the `DbContext` — nothing is
wrapped or re-implemented.

## Alternatives considered

**Per-aggregate repository interfaces** (`IDocumentRepository` with methods
like `GetByIdAsync`, `AddAsync`) would keep `Strata.Application` completely
free of any EF Core type. Rejected because `DbSet<T>` and `DbContext` already
are the Repository and Unit of Work patterns respectively, so a hand-rolled
interface over them re-implements an abstraction the framework already
provides; because returning materialized collections instead of `IQueryable`
breaks query composition and pushes the interface toward one method per query
shape as requirements grow; and because eager-loading (`Include`) and change
tracking get awkward once results cross an interface boundary that wasn't
designed around EF Core's semantics in the first place.

**`Strata.Application` referencing `Strata.Infrastructure`'s concrete
`AppDbContext` directly** was rejected outright: `Strata.Infrastructure`
already depends on `Strata.Application` (to implement its interfaces), so a
reference back would create a circular project dependency — the same failure
mode demonstrated hands-on earlier in Phase 0, where `dotnet add reference`
happily created one and only `dotnet build` caught it.

## Cost

`Strata.Application` takes a package reference to EF Core's abstractions
(for the `DbSet<T>` type), so "Application has zero EF Core awareness" is no
longer literally true — it knows the abstraction types but not the provider
(SQL Server), connection strings, or migrations, which stay confined to
`Strata.Infrastructure`. This also gives up the explicit, self-documenting
method list a repository interface provides, and `IApplicationDbContext` will
realistically have exactly one implementation for the project's lifetime —
which is consistent with, not a violation of, this project's own guardrail
against interfaces with no realistic second implementation.
