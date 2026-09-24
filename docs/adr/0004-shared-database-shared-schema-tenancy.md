# 0004. Shared database, shared schema, TenantId discriminator

Status: Accepted

## Decision

Strata is multi-tenant at the application layer, not the infrastructure
layer: every tenant's data lives in the same Azure SQL database, in the
same tables, distinguished by a `TenantId` column on every tenant-owned
row. There is one `Tenants` table (`Id`, `Name`, `CreatedAt`) recording
which tenants exist. Each `ApplicationUser` belongs to exactly one
`Tenant`. This ADR records the strategy; the current enforcement status is
described below.

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

Project policy:
**`IgnoreQueryFilters`, `ExecuteUpdate`, `ExecuteDelete`, and raw SQL must
not be used on tenant-owned data unless they receive a separate
tenant-isolation design review, explicit enforcement, and adversarial
integration tests.** `IgnoreQueryFilters` belongs on that list for the same
reason as the other two: a global query filter is a default a query
participates in, not a boundary it's kept inside of, and a single
documented call switches it off for any query that makes it. The current
`SaveChanges` interceptor does not see bulk writes, and no command
interceptor or database-level Row-Level Security is configured to cover
that gap — the policy above remains the containment until each path gets
its own enforcement design and adversarial tests.

A schema change (a new column, a new table) is felt by every tenant at
once — there is no way to roll a migration out to one tenant first, unlike
a database-per-tenant deployment where a bad migration is contained to
whoever it was applied to. Heavy read/write activity from one tenant
shares the same database compute and I/O as every other tenant (the
noisy-neighbour problem) with no isolation between them beyond whatever
Azure SQL's own resource governance provides. And critically: query filters
and interceptors do not enforce tenant-scoped reads for operational or
privileged database access — a raw query run through SSMS or a support script
with the SQL admin credential can read across tenants. Composite foreign keys
protect declared ownership, folder, document, and share relationships on
writes, including direct SQL writes, but they are not database-level read
access control.

## Enforcement status

The current application enforces tenancy through several independent
mechanisms:

- **Trusted tenant resolution.** The active `TenantId` for a request comes
  from a claim in a validated, signed JWT, exposed through a request-scoped
  tenant context — never accepted from a request body or query string.
- **EF Core global query filters** on tenant-owned entities, scoping
  EF LINQ query paths by default — ordinary reads, and the source-row
  selection of any `ExecuteUpdate`/`ExecuteDelete` — to the current
  request's tenant. Any query can opt out with `.IgnoreQueryFilters()`, so
  these filters are a default rather than a hard boundary. They also do not
  validate values a bulk statement assigns (see Costs and risks).
- **A `SaveChanges` interceptor** checks every `Added`, `Modified`, and
  `Deleted` tenant-owned entity before persistence. Existing rows are
  checked against their database values, so a detached entity cannot forge
  its tenant to pass validation.
- **Same-tenant document sharing.** The API rejects a recipient from another
  tenant using the same response as an unknown email. Composite foreign keys
  require `(DocumentId, TenantId)` to reference the same tenant's document
  and `(UserId, TenantId)` to reference a user in that tenant. The migration
  fails closed if existing cross-tenant shares are found; it does not delete
  or rewrite them.
- **Same-tenant ownership and folder/document relationships.** Composite
  foreign keys require each folder and document owner to belong to the
  resource's tenant, each parent folder to belong to the child folder's
  tenant, and each document folder to belong to the document's tenant. The
  migration checks existing rows first and fails closed without changing
  inconsistent data.
- **Adversarial two-tenant integration tests** exercise tenant filters,
  cross-tenant API access, and direct database writes that attempt to create
  mismatched share, owner, folder, or document relationships. The GitHub
  Actions workflow runs the full integration test project on pull requests.

Tenant isolation for declared relationships and application query paths is
now enforced and covered by integration tests. This shared-schema design
still does not provide database-level read access control: privileged SQL
access can read every tenant, and bulk writes or queries that bypass tenant
filters remain governed by the project policy above.
