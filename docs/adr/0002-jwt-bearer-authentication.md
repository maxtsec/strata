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
expire after one hour; there is no refresh-token flow yet. Every other
endpoint is `[Authorize]` and reads the caller's id back out of `sub`.

## Alternatives considered

**Cookie/session-based authentication** (ASP.NET Core's built-in cookie
auth) was rejected for this stage: Strata is a JSON API, not a server-rendered
app, so there is no natural place for a browser-managed cookie to live, and
session state would need a shared store the moment the API scales past one
instance — complexity not worth taking on before it is needed.

**Entra ID / Azure AD B2C** for end-user sign-in — noted in the project's own
tech stack as a possible later upgrade — was rejected for now. Its
tenant/app-registration model is real setup overhead that only pays for
itself once there is an actual enterprise-SSO story to tell, which fits
naturally once Phase 2 introduces multiple tenants; wiring it in for a
single-tenant walking skeleton with a handful of test accounts would be
solving a problem the project doesn't have yet.

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
