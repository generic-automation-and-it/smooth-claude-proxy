# LADR-01: Transparent Insertion — One Local Endpoint, Observer by Default

**Status:** Accepted

## Context

Claude Code is a fixed client we do not control: it speaks the Anthropic Messages API and resolves its base URL from `ANTHROPIC_BASE_URL`. We want to intercept its traffic to observe usage, switch accounts, and optionally redirect to alternate models — without forking the CLI, without a plugin, and without the user editing requests.

The risk is that any visible change to the default request path — a rewritten body, an injected header, a rejected "wrong shape" request — would surface as a Claude Code bug with no obvious cause, because the user believes they are talking to Anthropic directly.

## Decision

The proxy presents **one local HTTP endpoint** that Claude Code targets via `ANTHROPIC_BASE_URL`. For the default case — a real `claude-*` request — the proxy is a **pure observer**: it forwards the request to `api.anthropic.com` with the body and headers byte-for-byte intact (minus proxy-only metadata headers such as `x-user-label`), and streams the response straight back.

All routing intelligence is *additive and gated*. When routing is disabled, or the request is a plain `claude-*` message with no override, the request takes the inert passthrough path and the request body is never even parsed.

## Alternatives Considered

- **CLI plugin / fork.** Rejected — couples us to Claude Code's release cadence and internals; the whole point is to be CLI-agnostic.
- **Always parse and re-serialize the body.** Rejected — re-serialization can reorder keys, change whitespace, or drop unknown fields, breaking the byte-for-byte guarantee and risking subtle prompt-cache or signature differences. Parse only when a routing feature needs a field.
- **Reject non-conforming requests.** Rejected — the proxy must never be the thing that makes a previously-working request fail.

## Consequences

- The only client-side change is one environment variable; everything else is transparent.
- The default path must remain free of body mutation and unnecessary header changes. New features must be gated so they never touch the inert path. (See the guiding principle in the [README](../README.md).)
- Because the proxy is an observer, not an auth boundary, it does not validate tokens (see LADR-13 and the project AGENTS.md). It cannot reject a bad token — Anthropic does.

## Related

- **LADR-02** — Model-prefix dispatch (what distinguishes the inert path from the alternate routes).
- **LADR-10** — Never buffer (transparency includes streaming behaviour).
- **LADR-13** — Routing/tracking boundary (observation is confined to the Anthropic path).
