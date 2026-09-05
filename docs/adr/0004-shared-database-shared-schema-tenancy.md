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
each tenant's tables are namespaced apart. Rejected because EF Core has no
first-class support for a dynamic per-tenant schema — migrations, model
snapshots, and `DbContext` configuration all assume a fixed schema, so
this would mean either N sets of generated migrations to keep in sync or
hand-rolled schema-switching plumbing that fights the tool rather than
using it. It also doesn't remove the core hazard shared-schema has (a
missed filter is still a leak) while adding real operational complexity on
top.

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
ADR is forgotten. Read paths and write paths are two separate hazards, not
one: an EF Core global query filter only shapes `SELECT` statements, so it
does nothing to stop an `Added`, `Modified`, or `Deleted` entity from
being persisted under the wrong `TenantId` during `SaveChanges` — that gap
needs its own, separate enforcement.

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
  every read to the current request's tenant automatically.
- **A `SaveChanges` interceptor**, because query filters alone cover reads
  and nothing else — every `Added`, `Modified`, and `Deleted` entity
  needs a second, independent check at the point it's actually persisted.
- **Adversarial two-tenant integration tests**, run in CI, that create two
  tenants and actively try to make one reach the other's data — by id, by
  crafted request, by any path that can be thought of. Following this
  project's own principle: a mechanism nobody has tried to break is worse
  than no mechanism, because it invites trust it hasn't earned.

The PR that introduces this ADR adds only the `Tenants` table itself.
Nothing reads or writes through a tenant lens yet, and no existing
behaviour changes.
