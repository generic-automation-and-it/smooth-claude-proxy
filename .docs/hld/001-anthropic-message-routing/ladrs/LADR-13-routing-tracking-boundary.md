# LADR-13: Routing/Tracking Boundary — Only the Anthropic Non-Session Path Records Usage

**Status:** Accepted

## Context

The proxy tracks usage: it decodes the JWT to extract identity (no signature validation — it is an observer, not an auth boundary), captures Anthropic rate-limit headers from the response, and writes a `UserRecord` to the local database via a non-blocking channel. This tracking is meaningful **only for real Anthropic traffic**, because:

- Rate-limit headers (`anthropic-ratelimit-unified-*`) are emitted by Anthropic, not by alternate upstreams.
- The tracked credential is the client's Anthropic token; on alternate routes the client token isn't even sent upstream (the upstream's own token is used — LADR-04).
- In **override-session** mode, requests are intentionally proxied as a different cached identity, so recording them against the inbound token would be wrong.

The routing decision and the tracking decision must therefore be cleanly separated, so that reshaped traffic never pollutes usage data.

## Decision

Confine usage tracking to the **Anthropic path with no active override session**:

- **Identity extraction** (auth type, JWT email/name, label) runs early for all requests — it is cheap and feeds logging — but it is **not** a routing input.
- **Rate-limit capture + DB write** happen **only** after the YARP-forwarded Anthropic response, and **only** when a bearer token is present **and** no override session is active.
- **Alternate routes never track:** no rate-limit capture, no `UserRecord` write. (They have no Anthropic rate-limit headers and use upstream credentials.)
- **Override-session mode:** when an active session is set, the Anthropic path replaces the auth headers from the session and **skips the channel write entirely** — proxied session traffic is not recorded against the inbound token.

JWT decoding is identity-only and best-effort: extract `email`, then `name` (falling back to `sub`); opaque (non-JWT) tokens decode to null silently. **No signature validation** — the proxy doesn't hold Anthropic's keys and isn't an auth gate.

## Alternatives Considered

- **Track alternate routes too.** Rejected — there are no Anthropic rate-limit headers to capture, and the client token isn't the upstream credential, so any record would be meaningless or misattributed.
- **Track in override-session mode.** Rejected — session mode deliberately impersonates a cached identity; recording against the inbound token would corrupt per-token usage.
- **Make tracking a routing input.** Rejected — tracking is a side effect of the Anthropic path, not a determinant of where a request goes; coupling them would entangle two independent concerns.

## Consequences

- Usage data reflects only genuine Anthropic traffic under the inbound identity — clean and unambiguous.
- The routing core stays free of tracking logic; tracking is a tail effect on exactly one path.
- Identity extraction is universal (useful for logs on every request) but never influences dispatch.

## Related

- **LADR-01** — Transparent insertion (observer, not auth boundary).
- **LADR-02** — Model-prefix dispatch (the path selection that gates tracking).
- **LADR-04** — Dual API format (alternate routes use upstream credentials, not the client token).
- Project AGENTS.md — full user-tracking, channel, and override-session behaviour.
