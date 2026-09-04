# 0002. JWT bearer authentication over ASP.NET Core Identity

Status: Accepted

## Decision

User accounts are stored and password-hashed by ASP.NET Core Identity
(`AddIdentityCore<ApplicationUser>`, backed by `AppDbContext`), with
`ApplicationUser` keyed by `Guid` so it lines up with `Document.OwnerId` /
`Folder.OwnerId` with no string↔Guid conversion at any boundary.
Authentication itself is stateless: `POST /api/auth/register` and `/login`
issue a signed JWT (`JwtTokenGenerator`, HMAC-SHA256, a symmetric key from
`Jwt:SigningKey` — Key Vault deployed, user-secrets locally), carrying the
user's id as the `sub` claim, their email, and their Identity roles. Tokens
expire after one hour; there is no refresh-token flow yet. The business
resource endpoints (Folders, Documents) are `[Authorize]` and read the
caller's id back out of `sub`; `/health` and the register/login endpoints
themselves are necessarily anonymous.

## Alternatives considered

**Cookie/session-based authentication** (ASP.NET Core's built-in cookie
auth) was rejected for this stage. Note this isn't because it would force a
shared *session* store — ASP.NET Core's authentication cookie is a
self-contained encrypted ticket (via Data Protection), not a server-side
session, so no session store is required either way. What a cookie *would*
require across more than one instance is a shared Data Protection key
ring (Key Vault- or Blob-backed), so every instance can decrypt a ticket any
other instance issued — without it, a load-balanced request can land on an
instance that can't read a cookie a sibling instance wrote, and the user
gets spuriously logged out. The actual reasons to prefer a bearer JWT here:
Strata is a JSON API with no natural place for a browser-managed cookie to
live if the client isn't a browser; cookies are sent automatically by the
browser on every request to the origin, which is exactly the CSRF problem
(state-changing requests need an anti-forgery token to prove the request
was intentional) — a bearer token in an `Authorization` header isn't
auto-attached, so that whole defence isn't needed by construction; and one
shared JWT signing key is simpler to distribute across instances than
provisioning and rotating a Data Protection key ring, for a comparable
security posture at this scale.

**Microsoft Entra External ID** (the current name for what used to be
marketed as Azure AD B2C, which Microsoft no longer offers to new
customers) for end-user sign-in — noted in the project's own tech stack as
a possible later upgrade — was rejected for now. Its tenant/app-registration
model is real setup overhead that only pays for itself once there is an
actual enterprise-SSO story to tell. Note this is a separate concept from
Strata's own Phase 2 tenant model: an Entra *identity* tenant is about who
can sign in and how, while a Strata *application* tenant is about whose
data a signed-in user can see — introducing Entra wouldn't automatically
give Strata multi-tenancy, and Strata's tenant isolation wouldn't
automatically follow from an Entra tenant boundary either. They're worth
revisiting together, not assumed to arrive as a pair.

**Refresh tokens / sliding sessions** were deferred, not rejected: a hard
one-hour expiry is simple and sufficient to demonstrate the auth flow, but a
real client integration would need a refresh path so users aren't forced to
re-authenticate hourly.

## Cost

A JWT is valid until it naturally expires — there is no server-side
revocation list, so logging a user out or reacting to a compromised account
has no immediate effect on tokens already issued. This is the standard
tradeoff of stateless bearer tokens versus a server-tracked session, and it
is why the expiry window is kept short (one hour) rather than long-lived.

The signing key is a single symmetric secret: anyone who obtains
`Jwt:SigningKey` can mint arbitrary tokens for any user, so it has to be
treated with the same care as a password (Key Vault, never committed,
never logged). An asymmetric scheme (RS256) would let the signing key stay
fully private while a separate public key handles verification, at the cost
of managing a key pair instead of one shared secret — a reasonable upgrade
if this ever needs to be verified by a service that shouldn't be trusted to
mint tokens itself.

No refresh flow means every integration against this API today must handle
re-authentication once an hour, which is acceptable for a demo but not for a
production client experience.

Token validation currently sets `ValidateIssuer = false` and
`ValidateAudience = false` — there is exactly one issuer (this app) and one
audience (this app's own API) in the system today, so skipping those checks
isn't exploitable yet. The cost shows up the moment that stops being true:
if the signing key were ever shared with, or trusted by, a second service,
a token minted by that service would be accepted here with no origin check
at all. Setting an explicit `Issuer`/`Audience` and validating them is cheap
now and should happen before any second service enters the picture, rather
than being retrofitted under pressure later.
