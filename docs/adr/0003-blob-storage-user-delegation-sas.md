# 0003. File storage via user-delegation SAS, not proxied through the API

Status: Accepted

## Decision

A `Document`'s actual bytes live in Azure Blob Storage, one blob per
document, named by the document's `Guid` id. The API never streams file
content itself. On upload or download, `IFileStorage`
(`BlobFileStorage`) mints a short-lived (15-minute), user-delegation SAS URI
scoped to exactly that one blob and to exactly the permission needed
(Create/Write for upload, Read for download), and hands the URI back to the
client, which then talks to Blob Storage directly. The app authenticates to
Blob Storage itself with `DefaultAzureCredential` — the App Service's
managed identity when deployed, the developer's own `az login` locally — so
no static storage account key exists anywhere in the app or its
configuration.

## Alternatives considered

**Proxying bytes through the API** (client uploads/downloads hit an
ASP.NET Core endpoint, which streams to/from Blob Storage server-side) was
rejected: every byte would cross the App Service twice, doubling bandwidth
and compute cost for what is fundamentally a storage operation, and turning
the API into a throughput bottleneck for it. This does give up something
real, not nothing — a proxy re-runs authorization on every request, so a
revoked `DocumentShare` stops access immediately, while a SAS URI already
issued keeps working until it naturally expires (see Cost). The proxy
approach was rejected anyway: the residual-access window a SAS leaves open
is small and bounded (currently 15 minutes), and accepting it is cheaper
than doubling every request's bandwidth/compute cost to close a gap that
narrow.

**Storage-account-key-based SAS** was rejected in favour of a
user-delegation SAS. An account key is a long-lived, all-powerful static
secret — if it leaked, every blob in the account is compromised until the
key is rotated. A user-delegation SAS is instead signed by a short-lived key
obtained from Azure AD via the app's own identity, so there is no static
storage secret to leak in the first place, and access naturally stops the
moment the delegation key expires.

**A public or anonymous-read container with unguessable blob names** was
rejected outright. This is the actual boundary controlling who can read a
customer's document; it can never be anonymous, and a blob named after its
own database id gives no meaningful obscurity if the container were ever
misconfigured to be public.

## Cost

A SAS URI, once issued, is a bearer credential valid for its full window
regardless of what happens afterward — deleting a `DocumentShare` (or the
document itself) stops the *next* SAS from being minted, but does not
revoke one already handed out. Strata accepts up to ~15 minutes of residual
access after a revoke as the cost of not proxying every request. Azure does
offer a way to invalidate SAS tokens early — revoking the user-delegation
key (or the RBAC role behind it) — but that revocation isn't instant
(propagation delay) and isn't scoped to one SAS: it invalidates *every*
token signed by that key, including ones for other users' unrelated,
still-valid access. That blast radius is why Strata doesn't reach for it on
a single share revocation; it would be the right tool for something like
"lock this account out immediately," not "one recipient lost access to one
document."

The SAS URI is itself a bearer credential — anyone who obtains it has the
access it grants, no further authentication required — so it needs the same
handling discipline as a password: not logged, not left in browser history
longer than necessary, not pasted somewhere insecure. Nothing in the app
today enforces that discipline on the client side; it's a property of the
design to be aware of, not a control Strata currently implements.

SAS URIs are time-limited (15 minutes): an upload or download that takes
longer than that window fails and needs a freshly minted URI. Fine for
typical document sizes, but a very large file or a slow client connection
would need a wider window or a resumable-upload flow.

Because the API hands out a URI instead of proxying, it has no visibility
into whether an upload actually completed, its final size, or whether the
content type the client used matches what was declared when the URI was
requested — `Document.Size` and `Document.ContentType` are trusted at
creation time, not verified against the blob that eventually lands. A
production system would want a completion callback (Blob Storage's own
event grid notifications) or a periodic reconciliation job to catch
missing or mismatched uploads.

Minting a user-delegation key requires the app's identity to hold an RBAC
role on the storage account capable of requesting one (Storage Blob Data
Contributor) — more Azure role setup than a single static key would need,
though it is exactly the kind of access control a security-conscious system
should have regardless.
