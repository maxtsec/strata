# Strata — Design Document

> Companion documents: [Technical Document](technical.md) ·
> [設計文檔（中文）](design.zh.md) · [技術文檔（中文）](technical.zh.md)

## 1. What Strata is

Strata is a multi-tenant document collaboration platform: multiple customer
organisations share one deployment, and each organisation's data is isolated
from every other one's. A later phase adds an AI question-answering layer over
each tenant's own documents.

The name comes from the Australian *strata title* — one building, many
separately owned lots, shared structure underneath. That is multi-tenancy in
physical form.

This is a portfolio project. Its purpose is to demonstrate enterprise
architecture and a security-first engineering mindset, not to ship a product.
Where a shortcut was taken, this document says so rather than hiding it.

## 2. The central problem

Everything else in Strata exists to serve one question:

> When Tenant A's user makes a request, is there any path — by id, by crafted
> payload, by a future code change — that reaches Tenant B's data?

Two properties make this genuinely hard rather than merely tedious:

1. **Isolation is a property of application code, not infrastructure.** All
   tenants share one database and one set of tables (see §4). No firewall, no
   separate connection string, and no database boundary stops a query from
   reading across tenants. Only code does.
2. **Isolation has to survive people who never read this document.** A
   mechanism that works today but that a future feature can silently bypass
   isn't isolation; it is a convention with good intentions.

That second property is why the design leans on *defence in depth* (§5) and on
*adversarial tests* rather than on any single mechanism.

## 3. Architecture

Clean Architecture with dependencies pointing inward only:

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
| `Strata.Domain` | Entities and business rules. No EF Core, no Azure SDK, no ASP.NET. |
| `Strata.Application` | Use cases; declares the interfaces it needs (`IApplicationDbContext`, `IFileStorage`, `ICurrentTenant`). |
| `Strata.Infrastructure` | EF Core (`AppDbContext`, migrations), Blob Storage, ASP.NET Core Identity's `ApplicationUser`. Implements Application's interfaces. |
| `Strata.Api` | Controllers, request validation, DI wiring. The only project that references `Strata.Infrastructure`. |

**Why the direction matters here specifically.** The tenant isolation rules
live where the data access lives (`Strata.Infrastructure`), but the *concept*
of "who is the current tenant" is declared by `Strata.Application` as
`ICurrentTenant` and implemented by `Strata.Api` from the HTTP request. That
inversion is what lets the persistence layer enforce a rule whose source of
truth is an HTTP concern, without `Strata.Infrastructure` knowing anything
about HTTP.

The direction is enforced by `tests/Strata.Architecture.Tests`
(NetArchTest), not merely documented — `Strata.Domain` is asserted to have no
dependency on EF Core or ASP.NET at all.

### Deliberate non-abstractions

Strata does *not* have a repository layer over EF Core
([ADR 0001](adr/0001-no-repository-abstraction.md)). `DbSet<T>` is already the
Repository pattern and `DbContext` is already Unit of Work; wrapping them
re-implements a framework abstraction, breaks `IQueryable` composition, and
grows one method per query shape. `Strata.Application` instead declares a thin
`IApplicationDbContext` exposing `DbSet<T>` and `SaveChangesAsync`.

The general rule the project applies: an interface with exactly one
implementation and no realistic second one is not worth its cost. Being able
to explain why something was *not* abstracted is treated as more valuable than
demonstrating that it could have been.

## 4. Tenancy model

**Shared database, shared schema, `TenantId` discriminator column**
([ADR 0004](adr/0004-shared-database-shared-schema-tenancy.md)).

Every tenant-owned row carries a `TenantId` foreign key to a `Tenants` table.
Every `ApplicationUser` belongs to exactly one tenant.

Two alternatives were considered and rejected:

- **Database per tenant** gives the strongest isolation — a bug in a query
  filter cannot cross a database boundary. Rejected on economics and
  operations: every schema change becomes N deployments, and Azure SQL's
  per-database cost floor makes a handful of demo tenants needlessly
  expensive. It is the right answer when isolation is a contractual or
  regulatory requirement, which is not demonstrated here.
- **Schema per tenant** removes the shared-table hazard but not the tenant
  boundary hazard — the failure mode moves from "the row filter was forgotten"
  to "the schema switch was wrong". Rejected primarily because EF Core has no
  first-class dynamic per-tenant schema support: migrations and model
  snapshots assume a fixed schema, so it means fighting the tooling.

Shared schema was chosen partly because it is what the tooling supports well
and is the industry-common SaaS shape at this scale — and partly because it is
the version of multi-tenancy where the isolation guarantee must be *earned in
application code*. That is the lesson this project exists to demonstrate.

## 5. The isolation model: four independent layers

No single mechanism is trusted. Each layer below closes a different gap, and
each one is written down together with what it does **not** cover.

### Layer 1 — Trusted tenant identity at the authentication boundary

The active tenant comes from a `tenant_id` claim inside a signed, validated
JWT — never from a request body, query string, or header the client controls.

Validation happens in the JWT bearer `OnTokenValidated` event, i.e. at the
authentication boundary itself, not lazily inside a service. A token that is
otherwise perfectly valid but whose tenant claim is missing, malformed,
`Guid.Empty`, or present more than once fails authentication with 401 and
never reaches a controller. Rejecting *duplicate* claims matters: silently
taking the first of several would let a crafted token smuggle a second tenant.

This is a claim check, not a database lookup, so it stays cheap and does not
read tenant data in order to establish tenant identity.

*Does not cover:* anything that does not arrive over HTTP.

### Layer 2 — A request-scoped trusted tenant context

`ICurrentTenant` (declared in `Strata.Application`, implemented in
`Strata.Api` as `HttpContextCurrentTenant`) is the single source of truth for
"which tenant is this request acting as". Everything downstream — controllers
stamping new rows, query filters, the write interceptor — reads from it rather
than re-deriving tenant identity independently.

It resolves lazily and **fails closed**: reading `TenantId` without a valid
request throws rather than defaulting. A separate `IsAvailable` property
distinguishes two situations that must not be conflated:

| Situation | `IsAvailable` | Meaning |
|---|---|---|
| No HTTP request at all | `false` | Migrations, test/admin setup — there is no "current tenant" question to ask |
| A request exists but its claim is invalid | `true`, `TenantId` throws | Should be unreachable past Layer 1 — a bug or a tampered token |

Resolution is lazy specifically so that `AppDbContext` — which takes an
`ICurrentTenant` in its constructor — stays constructible outside an HTTP
request, which migrations and test setup require.

### Layer 3 — EF Core global query filters (reads)

`Folder`, `Document`, and `DocumentShare` each carry a filter of the form
`TenantId == currentTenant.TenantId`, applied to every LINQ query against
those sets by default.

Two design choices worth naming:

- **Explicit per-entity filters, not a reflection-based convention.** Three
  fixed entities do not justify expression-tree machinery that the compiler
  cannot check and a reader cannot see. A future fourth entity missing its
  filter is a visible one-line gap; reflection is the right tool for an
  architecture *test* that asserts coverage, not for the production wiring.
- **`Tenant` and `ApplicationUser` are deliberately not filtered.**
  Authentication must be able to find a user before a tenant is established,
  and share-recipient lookup is a separate concern (§7).

Because filters only apply to LINQ query paths, `FindAsync` was removed from
every security-sensitive lookup: `FindAsync` can return an already-tracked
entity straight from the change tracker without querying the database, and
therefore without the filter ever applying.

*Does not cover:* writes of any kind; any query that calls
`IgnoreQueryFilters()`.

### Layer 4 — A `SaveChanges` interceptor (writes)

Query filters rewrite `SELECT` and nothing else. `TenantWriteGuardInterceptor`
inspects every `Added`, `Modified`, and `Deleted` tenant-owned entity before
it is persisted, and refuses the whole `SaveChanges` if anything does not
belong to the current tenant.

The rules it applies, and why each exists:

| Entity state | Checked against | Attack it closes |
|---|---|---|
| `Added` | The value being written | A new row stamped with someone else's tenant |
| `Modified` | The **actual database row** *and* the value being written | Stealing a foreign row by id; retargeting an owned row into another tenant |
| `Deleted` | The **actual database row** | Deleting a foreign row by id |

The critical subtlety is that for `Modified` and `Deleted` the interceptor
does **not** trust the entity's own `TenantId`. An entity attached without
ever being queried — `Remove(new Document { Id = someId })`,
`Update(new Document { ... })` — carries whatever values the caller declared,
including a forged `TenantId` chosen to match the current tenant. EF Core's
generated `DELETE`/`UPDATE` predicate is the primary key, not `TenantId`, so a
forged claim would otherwise let a foreign row be deleted. The interceptor
therefore reads the row's real current state via
`EntityEntry.GetDatabaseValuesAsync()` and checks that instead.

It **validates only; it never assigns** a `TenantId`. Auto-stamping would
create a second place where tenant identity is decided, which is a worse
property than a few explicit assignments in create paths.

A tenant-owned write with no trusted tenant available **fails closed** rather
than being treated as an implicitly trusted admin bypass — the earlier
version of this design got that backwards, and the review that caught it is
part of why the rule is stated so explicitly here.

*Does not cover:* `ExecuteUpdate` / `ExecuteDelete` / raw SQL (§7).

### How the layers compose

For a request from Tenant A trying to reach a Tenant B document by id:

1. Layer 1 established that the caller really is acting as Tenant A.
2. Layer 3 makes the document invisible to the controller's lookup, so the
   request 404s before authorization even runs.
3. If some future code path bypassed the lookup and attached the row directly,
   Layer 4 refuses the write by consulting the database itself.
4. Owner authorization (§6) is a fourth, independent obstacle — but it is
   *not* counted as tenant isolation, because it answers a different question.

## 6. Authentication and authorization

**Authentication** ([ADR 0002](adr/0002-jwt-bearer-authentication.md)):
ASP.NET Core Identity stores and hashes credentials; authentication itself is
stateless via signed JWTs (HMAC-SHA256, one-hour expiry). `ApplicationUser` is
`Guid`-keyed so it lines up with `OwnerId` with no string↔Guid conversion at
any boundary.

Bearer tokens were preferred over cookies because Strata is a JSON API, and
because a bearer token in an `Authorization` header is not auto-attached by
the browser — which removes the CSRF problem by construction rather than
needing anti-forgery tokens to solve it.

**Authorization** is resource-based (`IAuthorizationHandler` against the
loaded entity), not role-string-based:

| Role | Download | Rename | Manage shares |
|---|---|---|---|
| Owner | yes | yes | yes |
| Member (via share) | yes | yes | no |
| Viewer (via share) | yes | no | no |

Missing and unauthorised resources both return 404 rather than 403, so a
response cannot be used to probe which ids exist (anti-enumeration).

**Owner authorization is not tenant isolation.** It happens to block most
cross-tenant access today, because owners are per-user, but it answers "is
this the right user?" and never consults `TenantId` at all. Treating it as an
isolation boundary would be the exact category error this design is built to
avoid — hence layers 3 and 4 exist independently of it.

## 7. Known gaps

Stated plainly, because a gap that is written down is a design decision and a
gap that is not is a defect.

- **Cross-tenant shares are not rejected at creation.** Tenant A can still
  create a share naming a Tenant B user. The share is stamped with the
  *document's* tenant (never the recipient's), so it does not launder the
  document into another tenant, and the read filter makes it unusable for the
  recipient — it is a dead row, not a leak. Same-tenant relationship
  enforcement is the next planned piece of work.
- **`ExecuteUpdate` / `ExecuteDelete` / raw SQL bypass both layers 3 and 4.**
  Set-based statements never load entities into the change tracker, so the
  interceptor cannot see them; a query filter constrains which rows such a
  statement reads, but not the values it assigns. Project policy: these, and
  `IgnoreQueryFilters`, must not be used on tenant-owned data without a
  separate tenant-isolation design review, explicit enforcement, and
  adversarial tests. Today the containment is policy, not code.
- **Operational and privileged database access bypasses everything.** A query
  run through SSMS or a support script with the SQL admin credential is
  outside the application's query surface entirely. Query filters and
  interceptors are not database-level access control.
- **The write check is validate-before-save, not a SQL-level predicate.**
  There is a theoretical window between the check and the write. It is
  acceptable at this scale, but if tenant transfer ever becomes a feature, the
  race needs revisiting.
- **JWTs cannot be revoked before expiry.** Standard stateless-bearer
  tradeoff; mitigated by a one-hour window rather than solved.
- **An issued SAS URI survives a revoked share** for up to its 15-minute
  window ([ADR 0003](adr/0003-blob-storage-user-delegation-sas.md)). Accepted
  deliberately rather than doubling every byte's bandwidth and compute cost by
  proxying file traffic through the API.
- **`ValidateIssuer` / `ValidateAudience` are off.** Safe while exactly one
  issuer and one audience exist; must be fixed before any second service
  shares or trusts the signing key.

## 8. Phases

| Phase | Scope | Status |
|---|---|---|
| 0 | Walking skeleton: solution structure, `/health`, deployed to Azure, CI/CD green, Key Vault | Complete |
| 1 | Single-tenant core: users, folders, documents, auth, Blob Storage upload/download, roles, share links | Complete |
| 2 | Multi-tenant retrofit: `Tenant`, `TenantId` columns, tenant resolution, query filters, write interceptor, adversarial tests | In progress |
| 3 | Notification subsystem: `INotificationChannel`, background dispatch, retry with backoff, idempotency | Planned |
| 4 | Production hardening: structured logging, global exception handling, rate limiting | Planned |
| 5 | Multi-tenant RAG: ingestion, tenant-filtered vector retrieval, second pre-generation check, cost/quota | Planned |
| 6 | Packaging: diagrams, screenshots, spoken walkthroughs | Planned |

Phase 1 was built deliberately single-tenant so that Phase 2 would be a real
retrofit. Doing the retrofit — including the staged, fail-closed data
migration that backfills existing rows from their real relationships — is part
of the intended lesson, not an accident of sequencing.

**Phase 2 remaining:** same-tenant relationship enforcement; the complete
adversarial two-tenant test matrix in CI; finalising the tenant-isolation ADR
once the enforcement design is settled.

Phase 5 is where isolation gets genuinely harder: a vector store is a weaker
system than a relational one, many of them filter *after* search rather than
before, and a post-search filter means another tenant's chunks were already
retrieved. The planned rule is that the tenant filter must be applied before
the search, and every retrieved chunk re-verified before it enters a prompt —
the same defence-in-depth reasoning as layers 3 and 4, applied to a system
with fewer guarantees. **The LLM is never a security boundary**: every
isolation decision is made in code, before or after the model, never by it.

## 9. Decision record index

| ADR | Decision |
|---|---|
| [0001](adr/0001-no-repository-abstraction.md) | No repository abstraction over EF Core |
| [0002](adr/0002-jwt-bearer-authentication.md) | JWT bearer authentication over ASP.NET Core Identity |
| [0003](adr/0003-blob-storage-user-delegation-sas.md) | File storage via user-delegation SAS, not proxied through the API |
| [0004](adr/0004-shared-database-shared-schema-tenancy.md) | Shared database, shared schema, `TenantId` discriminator |

Each ADR records what was decided, what was considered instead, and what the
decision costs — written at the moment of deciding, while the reasoning was
still fresh.
