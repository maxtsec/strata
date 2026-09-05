# 0004. Shared database, shared schema, TenantId discriminator

Status: Accepted

## Decision

Strata is multi-tenant at the application layer, not the infrastructure
layer: every tenant's data lives in the same Azure SQL database, in the
same tables, distinguished by a `TenantId` column on every tenant-owned
row. There is one `Tenants` table (`Id`, `Name`, `CreatedAt`) recording
which tenants exist. Each `ApplicationUser` will belong to exactly one
`Tenant`. This ADR records the strategy; the `TenantId` columns themselves,
tenant resolution, and the enforcement mechanisms below are deliberately
out of scope for the PR that introduces this ADR (see "Planned defence in
depth").

## Alternatives considered

**Database per tenant** (a separate Azure SQL database provisioned for
each customer organisation) gives the strongest physical isolation — a
bug in a query filter literally cannot leak into a different database. It
was rejected here because it doesn't fit a portfolio project's economics
or operational shape: provisioning, migrating, and monitoring N databases
instead of one means every schema change is N deployments instead of one,
and Azure SQL's per-database cost floor makes a handful of demo tenants
needlessly expensive. It's the right answer when a tenant's isolation or
compliance requirements demand it (regulatory data residency, a
contractual guarantee of physical separation) — not demonstrated needs
here.

**Schema per tenant** (one database, one schema per tenant, each with its
own copy of every table) sits in between: still one database to run, but
each tenant's tables are namespaced apart. This genuinely removes the
specific hazard shared-schema has — there is no shared `Documents` table
where a forgotten `TenantId` row filter leaks a row across tenants,
because there is no shared table at all. What it doesn't remove is the
hazard of resolving and mapping to the *correct* schema for a given
request in the first place: selecting the wrong schema, an explicit
cross-schema query, or privileged database access can all still cross a
tenant boundary — the failure mode moves from "the row filter was
forgotten" to "the schema switch was wrong," it doesn't disappear.
Rejected here because EF Core has no first-class support for a dynamic
per-tenant schema — migrations, model snapshots, and `DbContext`
configuration all assume a fixed schema, so this would mean either N sets
of generated migrations to keep in sync or hand-rolled schema-switching
plumbing that fights the tool rather than using it. That operational and
tooling complexity, not a belief that it's less safe, is why Strata
rejects it.

**Shared schema was chosen** because it is what the tooling actually
supports well (one `DbContext`, one migration history, ordinary EF Core
query composition), it is the industry-common shape for SaaS products at
this scale, and — most relevant to this project's purpose — it is the
version of multi-tenancy where the isolation guarantee has to be earned in
application code rather than handed to you by infrastructure. That is
explicitly the lesson Phase 2 exists to teach.

## Costs and risks

Every tenant-owned table and every query against it must participate in
tenant scoping — there is no structural barrier stopping a query from
reading across tenants, only a discipline that has to be applied
consistently everywhere, forever, including in code written after this
ADR is forgotten. Read paths and write paths are two separate hazards, and
writes themselves split into two further paths EF Core treats completely
differently:

- **Change-tracked writes** — an entity added, modified, or removed via
  the change tracker and persisted through `SaveChangesAsync`. A
  `SaveChanges` interceptor sees every one of these and can inspect or
  reject them before they reach the database.
- **Set-based bulk writes** — `ExecuteUpdate`/`ExecuteDelete`, which
  translate an EF LINQ query straight into a single `UPDATE`/`DELETE`
  statement without ever loading entities into the change tracker. They
  bypass `SaveChanges` entirely, so a `SaveChanges` interceptor cannot see
  or block them. A global query filter still scopes which *rows* such a
  statement can touch, but it constrains the query's source set, not the
  values the statement assigns — a tenant-scoped `ExecuteUpdate` against
  Tenant A's rows could still set `TenantId` to Tenant B on every row it
  touches, and nothing described in this ADR would catch that.

Project policy, effective now even though nothing enforces it yet:
**`IgnoreQueryFilters`, `ExecuteUpdate`, `ExecuteDelete`, and raw SQL must
not be used on tenant-owned data unless they receive a separate
tenant-isolation design review, explicit enforcement, and adversarial
integration tests.** `IgnoreQueryFilters` belongs on that list for the same
reason as the other two: a global query filter is a default a query
participates in, not a boundary it's kept inside of, and a single
documented call switches it off for any query that makes it. No command
interceptor, wrapper abstraction, or database-level Row-Level Security is
introduced in this PR to cover that gap — the policy above is the
containment for now, and closing it properly is later work, not something
to improvise on the day someone reaches for one of these.

A schema change (a new column, a new table) is felt by every tenant at
once — there is no way to roll a migration out to one tenant first, unlike
a database-per-tenant deployment where a bad migration is contained to
whoever it was applied to. Heavy read/write activity from one tenant
shares the same database compute and I/O as every other tenant (the
noisy-neighbour problem) with no isolation between them beyond whatever
Azure SQL's own resource governance provides. And critically: none of the
mechanisms below are enforced for operational or privileged database
access — a raw query run through SSMS, a support script connecting
directly with the SQL admin credential, or a future background job that
doesn't go through `AppDbContext`'s configured filters bypasses all of it.
Query filters and interceptors protect the application's own query
surface; they are not a database-level access control.

## Planned defence in depth (not yet implemented)

None of the following exist yet — they are the subject of later PRs in
this phase, listed here so the plan is visible before it's built:

- **Trusted tenant resolution.** The active `TenantId` for a request will
  come from a claim in a validated, signed JWT, exposed through a
  request-scoped tenant context — never accepted from a request body or
  query string.
- **EF Core global query filters** on every tenant-owned entity, scoping
  EF LINQ query paths by default — ordinary reads, and the source-row
  selection of any `ExecuteUpdate`/`ExecuteDelete` — to the current
  request's tenant. "By default" is doing real work in that sentence: any
  query can opt out with `.IgnoreQueryFilters()`, a documented, one-line
  EF Core call, which is why the policy below now covers it explicitly
  rather than treating the filter as a hard boundary. It also does not
  validate values a bulk statement assigns (see Costs and risks); it only
  narrows which rows a query can touch, and only when nothing has
  disabled it.
- **A `SaveChanges` interceptor**, protecting only change-tracked writes
  that pass through `SaveChangesAsync` — every `Added`, `Modified`, and
  `Deleted` entity gets a second, independent check at the point it's
  actually persisted. It has no visibility into `ExecuteUpdate`,
  `ExecuteDelete`, or raw SQL, which is why those remain governed by
  policy rather than code until they get their own design work.
- **Adversarial two-tenant integration tests**, run in CI, that create two
  tenants and actively try to make one reach the other's data — by id, by
  crafted request, by any path that can be thought of. Following this
  project's own principle: a mechanism nobody has tried to break is worse
  than no mechanism, because it invites trust it hasn't earned.

The PR that introduces this ADR adds only the `Tenants` table itself.
Nothing reads or writes through a tenant lens yet, and no existing
behaviour changes.
