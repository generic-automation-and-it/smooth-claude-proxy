# LADR-11: Alternate Routes Bypass YARP — Direct HttpClient; Anthropic Uses the Catch-All

**Status:** Accepted

## Context

The proxy uses YARP (a reverse-proxy library) with a single catch-all route to Anthropic. YARP is excellent for transparent passthrough: it preserves headers, handles streaming, and adds an activity timeout. But the alternate routes need things YARP's static-config model makes awkward per-request: a different destination host, **rewritten auth headers** (upstream token, not the client's), a possibly **rewritten body** (model swap, cache injection, full conversion), and a **synthesized response** (the converted-path handler builds SSE from scratch).

We need to decide whether to express the alternate routes as additional YARP clusters/transforms or as direct calls.

## Decision

- **Anthropic path** → **YARP catch-all.** A single `{**catch-all}` route forwards to `api.anthropic.com` with headers preserved and a 10-minute activity timeout (the framework default of ~100s would kill long streaming generations). This is the transparent observer path (LADR-01).
- **Alternate paths** → **direct `HttpClient`, bypassing YARP entirely.** The router builds an outbound `HttpRequestMessage` to `{base}{path}{query}` (passthrough) or `{base}/v1/chat/completions` (conversion), sets the upstream auth headers, attaches the (possibly rewritten) body, sends with response-headers-read completion, and streams/translates the reply itself.

The endpoint registration order matters: explicit method-mapped endpoints (`/override-model`, `/logins`, etc.) and the forwarding middleware are registered **before** `MapReverseProxy()`, so they take precedence over the catch-all. The catch-all only handles what falls through to the Anthropic path.

Per-request upstream timeout on the alternate paths is generous (10 minutes) to match long generations.

## Alternatives Considered

- **Express alternate routes as YARP clusters with transforms.** Rejected — per-request body rewriting, auth substitution, and *synthesized* responses (the converted handler emits SSE that no upstream produced) fall outside YARP's transform model or require contortions. Direct `HttpClient` is clearer and fully under our control.
- **Route everything through `HttpClient`, drop YARP.** Rejected — YARP gives the Anthropic passthrough robust header preservation, streaming, and timeout handling for free; reimplementing that for the transparent path adds risk for no gain.

## Consequences

- Two forwarding mechanisms coexist: YARP for the transparent Anthropic path, direct `HttpClient` for everything reshaped. Each is used where it fits.
- The alternate paths own their own timeout, error mapping (LADR-12), and streaming discipline (LADR-10) — none of which inherit from YARP.
- Endpoint/middleware registration order is load-bearing: the catch-all must be last.

## Related

- **LADR-01** — Transparent insertion (YARP path).
- **LADR-04** — Dual API format (both dialects are direct-HttpClient).
- **LADR-10** — Streaming (direct paths must stream explicitly).
- **LADR-12** — Error mapping (direct paths synthesize error envelopes).
