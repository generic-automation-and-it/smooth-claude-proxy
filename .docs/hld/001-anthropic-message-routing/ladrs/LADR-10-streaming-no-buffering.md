# LADR-10: Never Buffer — SSE Streaming End-to-End on Every Path

**Status:** Accepted

## Context

Claude Code consumes the Anthropic Messages API as a **Server-Sent Events** stream. It expects `content_block_delta` events to arrive incrementally and renders them as they come. If the proxy buffers the full response before sending it, the CLI receives nothing until the upstream completes — which, for a long generation, means it **hangs indefinitely with no error**. This is the single most damaging failure mode in the whole proxy, because it looks like a freeze, not a bug.

Buffering can creep in implicitly: ASP.NET response body features, intermediate `MemoryStream`s, or "read the whole response then write it" patterns all defeat streaming.

## Decision

**Disable response-body buffering on every routing path before the first write, and stream the upstream response to the client as it arrives.**

- **Anthropic path (YARP):** YARP forwards with buffering disabled; the SSE passes through unmodified.
- **`anthropic` passthrough:** request the upstream with response-headers-read completion, then copy the response stream to the client incrementally.
- **`openai` verbatim:** same — stream the upstream response straight back.
- **`openai` converted (handler):** the handler writes Anthropic SSE events and **flushes after each event**. The Qwen handler is the deliberate exception to *upstream* streaming — it reads the upstream's full JSON because the request set `stream=false` — but it still **emits** SSE to the client incrementally with a flush per event, so the client sees a normal stream.

"Read headers, then stream the body" is the required pattern for every direct-HttpClient path. Never "read the full body, then write".

## Alternatives Considered

- **Buffer then forward (simpler code).** Rejected — directly causes the hang-with-no-error failure. Non-negotiable.
- **Buffer only small responses.** Rejected — there's no reliable size signal before streaming, and the cost of getting it wrong (a frozen CLI) is too high.

## Consequences

- Every path must use header-read completion and incremental copy/flush; no full-response materialization on the hot path.
- The one place a full upstream body is read (Qwen, `stream=false`) is justified by the handler's design and still streams to the client.
- This decision constrains how new handlers and upstreams are added: a streaming upstream handler must consume the upstream SSE incrementally, not buffer it.

## Related

- **LADR-08** — Response handlers (must flush per event).
- **LADR-01** — Transparency (streaming behaviour is part of being indistinguishable from Anthropic).
- Project AGENTS.md non-negotiable: "Never buffer responses — `DisableBuffering()` is required."
