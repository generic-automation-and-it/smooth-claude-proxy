# LADR-12: Anthropic-Shaped Error Mapping

**Status:** Accepted

## Context

When something goes wrong on an alternate route — the upstream is unreachable, the context is too long, no response handler exists, or the upstream returns a non-2xx — the client is still Claude Code, which parses errors as **Anthropic error envelopes**. A raw upstream error body, a bare 500, or a connection-exception stack trace is either unparseable by the CLI or actively misleading. Errors must be shaped so the client (and the operator reading logs) can act on them.

## Decision

Map alternate-route failures to deterministic statuses with **Anthropic-shaped error bodies** where the client will parse them:

| Condition | Status | Body | Operator signal |
|---|---|---|---|
| Upstream connection failed (`anthropic` passthrough or `openai`) | **502** | `{"error":"LLM unreachable at {base}: {detail}"}` | Is the alternate upstream running/reachable? |
| Upstream non-2xx whose body indicates context overflow (mentions "context length"/"context window"/"initial prompt") | **400** | Anthropic error envelope: `invalid_request_error`, message instructing to `/compact` or use a larger-context model | Conversation too long for the configured model |
| Upstream non-2xx (other) | **relayed status** | the upstream error body | Inspect the upstream's own error |
| Converted path, no response handler for the model | **501** | Anthropic error envelope: `api_error`, message to register a handler / use `anthropic` passthrough / disable `StripNonClaudeModels` | Model unsupported on the converted path |
| Unexpected exception during response handling | **500** | `{"error":"Response handling failed: {detail}"}` (only if response not already started) | A genuine bug |

The Anthropic error envelope shape (used where the client parses errors):

```json
{
  "type": "error",
  "error": { "type": "invalid_request_error", "message": "…" }
}
```

Additional rules:
- **Never emit a 500 for a missing handler** — that case is the explicit 501 above (LADR-08).
- Once the response has started streaming, an error cannot change the status; log it and stop. Only synthesize an error body if `Response.HasStarted` is false.
- Any upstream response with status ≥ 400 (on any path, including Anthropic) logs a **Warning** reminding the operator that traffic is going through the proxy — check that it's running and the key is valid.

## Alternatives Considered

- **Relay every upstream error verbatim.** Rejected — connection failures and missing handlers have no upstream body to relay, and context-overflow errors are far more useful translated into the actionable `/compact` guidance.
- **Always 500 on failure.** Rejected — collapses distinct, separately-actionable conditions (unreachable vs overflow vs unsupported) into one unhelpful status.
- **Plain-text errors.** Rejected for client-facing cases — Claude Code parses the Anthropic envelope; plain text degrades the CLI's error display.

## Consequences

- Failures are diagnosable from the status code alone, and the bodies tell the operator what to do.
- The context-overflow translation turns a cryptic upstream rejection into the standard Claude Code remedy (`/compact`).
- Distinguishing 502/400/501/500 keeps "infra down", "prompt too big", "model unsupported", and "proxy bug" from being confused.

## Related

- **LADR-08** — Missing handler → 501 (not 500).
- **LADR-10** — Streaming (can't restate status after the stream starts).
- **LADR-11** — Direct HttpClient paths own their error synthesis.
