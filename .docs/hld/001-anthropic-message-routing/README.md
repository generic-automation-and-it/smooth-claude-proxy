# Anthropic Message Routing — High-Level Design

## Intent

This document is the high-level design (HLD) for the **message-routing subsystem** of the Smooth Claude Proxy — the part of the request pipeline that decides, for every inbound Claude Code request, *where the request goes and in what shape it arrives*. It is written as a **spec for intent-driven / spec-driven development**: an AI coder (or a human) should be able to reconstruct the routing behaviour from this document alone, without reading the existing implementation. It describes contracts, decisions, and observable behaviour — not code. Where a data shape matters, it is given as a JSON schema, because the wire format *is* the contract.

The proxy as a whole captures identity, tracks usage, and supports session override; those concerns are documented elsewhere. This HLD is scoped to **routing**: the decision tree that selects a destination and a transformation for each message, and the three downstream paths that result.

## The Problem

Claude Code speaks exactly one protocol — the **Anthropic Messages API** (`POST /v1/messages`, SSE streaming, `model` + `messages` + `tools` + `system`). It always sends `claude-*` model identifiers and expects Anthropic-shaped SSE back. We want one local endpoint (`ANTHROPIC_BASE_URL`) to transparently serve three different realities:

1. **Real Anthropic** — the default. The proxy is an invisible observer; the request reaches `api.anthropic.com` byte-for-byte.
2. **An Anthropic-compatible alternate upstream** (e.g. opencode.ai Zen, MiniMax) — speaks `/v1/messages` natively, so the body passes through with only minimal, surgical edits.
3. **An OpenAI-compatible upstream** (e.g. LM Studio, a local Qwen) — speaks `/v1/chat/completions`, so the request must be *converted* on the way in and the reply *converted back* to Anthropic SSE on the way out.

The router's job is to make all three indistinguishable to Claude Code, and to make the choice between them a matter of **configuration and the inbound `model` field**, never a client change.

## Key Goals

### 1. Transparent insertion — Claude Code never knows

The only client-side change is `ANTHROPIC_BASE_URL`. No request is rejected for being "wrong shape". The default behaviour (pure Anthropic passthrough) is byte-for-byte identical to talking to Anthropic directly. See [LADR-01](./ladrs/LADR-01-transparent-insertion.md).

### 2. Routing is a function of the model prefix

The dispatch primitive is dead simple and inspectable: a model that starts with `claude-` goes to Anthropic; anything else goes to the alternate upstream. Per-family **default-model overrides** layer on top, letting an operator transparently redirect (say) every `claude-haiku-*` request to a cheaper local model without Claude Code ever emitting a non-Claude model name. See [LADR-02](./ladrs/LADR-02-model-prefix-routing.md) and [LADR-03](./ladrs/LADR-03-per-family-default-override.md).

### 3. One inbound contract, two upstream dialects

The proxy owns the **anti-corruption translation** between the Anthropic Messages dialect and the OpenAI Chat Completions dialect, in both directions. Which dialect the upstream speaks is a single setting (`ApiFormat`). See [LADR-04](./ladrs/LADR-04-dual-api-format.md).

### 4. Safe-by-default transformation

Conversion is lossy and risky; passthrough is safe. So the default is to **forward verbatim** and only run the full conversion-and-slimming pipeline when explicitly opted in (or when a model that structurally requires it — Qwen — is targeted). See [LADR-05](./ladrs/LADR-05-strip-gate-verbatim-default.md).

### 5. Streaming is sacred

Claude Code is an SSE client. Every path streams; response buffering is disabled everywhere. A buffered response hangs the CLI indefinitely with no error. See [LADR-10](./ladrs/LADR-10-streaming-no-buffering.md).

## Core Separation of Concerns

> **Claude Code owns the conversation. The router owns where that conversation is fulfilled and how it is reshaped to fit the chosen fulfiller. These are different concerns, and isolating the routing decision from both the client and the upstreams is what lets us add or swap upstreams without touching either side.**

The client is fixed (it is Anthropic's CLI). The upstreams are swappable. The router is the stable seam between them: it absorbs every difference in protocol, authentication, and capability so that neither the client nor any upstream has to know the other exists.

## Guiding Principle — The default path must be inert

> **When routing is doing nothing special, it must do *nothing* — observe and forward, byte-for-byte.**

Every feature in this subsystem is gated so that the common case (a real `claude-*` request to Anthropic) takes the cheapest, most transparent path. Body parsing only happens when routing is enabled. Conversion only happens when opted in. Cache injection only happens on the passthrough path. The router earns its complexity only on the requests that need it.

---

## Diagrams

- [C4 Containers](./diagrams/c4-containers.md) — system context, container view, and the routing-relevant inventory.
- [Routing Decision Flow](./diagrams/routing-decision-flow.md) — the dispatch decision tree and a sequence diagram per downstream path.

## Implementation Plan

- [`impl/IMPLEMENTATION_PLAN.md`](./impl/IMPLEMENTATION_PLAN.md) — phased reconstruction of the routing subsystem from scratch (decision core → Anthropic passthrough → OpenAI conversion → response translation), with NFRs and acceptance gates.

## LADRs (Lightweight Architecture Decision Records)

All records below are **Accepted** — they describe behaviour that is implemented and in use. Treat them as the authoritative contract; flag any proposed change that contradicts one rather than silently overriding it.

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-transparent-insertion.md) | Transparent insertion — one local endpoint, observer by default | Accepted |
| [LADR-02](./ladrs/LADR-02-model-prefix-routing.md) | Model-prefix dispatch — `claude-*` → Anthropic, else → alternate | Accepted |
| [LADR-03](./ladrs/LADR-03-per-family-default-override.md) | Per-family default-model override — redirect a Claude family to an alternate model | Accepted |
| [LADR-04](./ladrs/LADR-04-dual-api-format.md) | Dual upstream API format — `anthropic` passthrough vs `openai` conversion | Accepted |
| [LADR-05](./ladrs/LADR-05-strip-gate-verbatim-default.md) | Verbatim-by-default strip gate — conversion is opt-in; Qwen always converts | Accepted |
| [LADR-06](./ladrs/LADR-06-prompt-cache-injection.md) | Prompt-cache injection on passthrough — inject ephemeral `cache_control` when absent | Accepted |
| [LADR-07](./ladrs/LADR-07-count-tokens-interception.md) | `count_tokens` interception for alternate routes — local estimate | Accepted |
| [LADR-08](./ladrs/LADR-08-keyed-response-handlers.md) | Keyed per-model response handlers — open-closed, explicit 501 when missing | Accepted |
| [LADR-09](./ladrs/LADR-09-two-tier-config.md) | Two-tier configuration — immutable startup options seed mutable runtime settings | Accepted |
| [LADR-10](./ladrs/LADR-10-streaming-no-buffering.md) | Never buffer — SSE streaming end-to-end on every path | Accepted |
| [LADR-11](./ladrs/LADR-11-bypass-yarp-for-alternate.md) | Alternate routes bypass YARP — direct HttpClient; Anthropic uses the YARP catch-all | Accepted |
| [LADR-12](./ladrs/LADR-12-anthropic-shaped-errors.md) | Anthropic-shaped error mapping — upstream failures become Anthropic error envelopes | Accepted |
| [LADR-13](./ladrs/LADR-13-routing-tracking-boundary.md) | Routing/tracking boundary — only the Anthropic non-session path records usage | Accepted |
