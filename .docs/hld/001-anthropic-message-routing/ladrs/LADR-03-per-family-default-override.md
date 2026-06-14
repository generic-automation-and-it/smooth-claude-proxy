# LADR-03: Per-Family Default-Model Override

**Status:** Accepted

## Context

Operators sometimes want a whole Claude *family* transparently served by a different model — e.g. send every `claude-haiku-*` request to a cheap local model, or every `claude-opus-*` to an alternate provider — without Claude Code ever emitting a non-Claude model name (it won't; it only knows `claude-*`). Model-prefix dispatch (LADR-02) alone can't do this, because `claude-*` always routes to Anthropic.

We need a redirect that (a) keys on the Claude *family*, not an exact model id (model ids carry version suffixes that change); (b) is empty/off by default so it never surprises; and (c) flows through the same dispatch predicate rather than being a special case.

## Decision

Provide a **per-family default-model override** for four families, matched by prefix on the inbound `model`:

| Family prefix (case-insensitive) | Override target setting |
|---|---|
| `claude-fable` | Fable override |
| `claude-opus` | Opus override |
| `claude-sonnet` | Sonnet override |
| `claude-haiku` | Haiku override |

Resolution: if the inbound `model` starts with a family prefix **and** that family's override is non-empty, return the override target; otherwise return null (no redirect). When an override resolves, the router **swaps the `model` field to the override target** and marks `overrideApplied = true`, which forces the alternate route via the `OR overrideApplied` clause in `isLlmRoute` (LADR-02).

The model swap is a **key-order-preserving** rewrite of the top-level `model` property only; non-object JSON bodies are left unchanged.

Empty or null override = no redirect; the family passes through to Anthropic as normal. This is the default for all four families.

## Alternatives Considered

- **Exact model-id mapping.** Rejected — model ids carry version/date suffixes (e.g. `claude-haiku-4-5-20251001`) that change; a prefix match on the family is stable.
- **A single global "redirect all Claude" switch.** Rejected — too coarse; operators want to redirect cheap families (haiku) while leaving expensive ones on Anthropic, or vice versa.
- **Rewriting the model only at the upstream call site.** Rejected — the swap must happen *before* the dispatch predicate so the existing model-prefix logic naturally redirects it; doing it later would require a parallel "force alternate" flag threaded through every branch.

## Consequences

- Operators can transparently remap a Claude family to any alternate model with one config value, per family, with no client change.
- The override composes with both alternate dialects: on the `anthropic` passthrough the swapped model is written into the forwarded body; on the `openai` path it becomes the rewritten model for `/v1/chat/completions`.
- The four families are enumerated explicitly. Adding families means extending the enumeration (see Migration Plans in the AGENTS doc) — acceptable while the family set is small and stable.
- Override targets live in the runtime settings (seeded from startup options — LADR-09), so they can be changed without restart via the same configuration surface.

## Related

- **LADR-02** — Model-prefix dispatch (override feeds the same predicate).
- **LADR-09** — Two-tier config (where override targets are stored and seeded from).
- **LADR-04** — Dual API format (how the swapped model is forwarded per dialect).
