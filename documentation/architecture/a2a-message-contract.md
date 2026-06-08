# A2A Message Contract

PR-7 ships the harness Agent-to-Agent (A2A) surface — the common dispatch
shape every inter-skill call goes through, whether the callee is in the
same process or a remote one. The shape is identical across transports
so a consumer can split a skill into its own process later without
touching call sites.

## Why not a thin wrapper over MAF A2A?

The plan calls for "thin wrapper over MAF" but Microsoft Agent
Framework 1.9.0 (current pin — `Microsoft.Agents.AI`) does **not** ship
an A2A client/server surface. The Linux Foundation A2A protocol is
young (June 2025) and MAF has not yet published primitives that the
harness could wrap. PR-7 therefore ships a harness-native A2A surface
with the same goals — wire shape, identity propagation, OTel span
linking, mutual-TLS-plus-JWT auth — and pins to MAF 1.9.0 via a
canary test (`A2AVersionPinTests`) that fails the moment MAF adds an
A2A surface, so the harness can switch to wrapping it.

## Envelope

Every A2A call carries an `A2AEnvelope` plus a payload. The envelope is
JSON-serializable with explicit camelCase property names — no
serializer-config drift between processes.

```json
{
  "schemaVersion": 1,
  "correlationId": "ec0b3a4d6e6f4b5a8e9f0c1d2e3f4a5b",
  "callerAgentId": "sre-agent",
  "callerKind": "FederatedCredential",
  "calleeAgentId": "workspace-agent",
  "calleeSkill": "search-tickets",
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "extensions": { "tenantId": "contoso" }
}
```

| Field | Required | Purpose |
| --- | --- | --- |
| `schemaVersion` | yes | Envelope version; current `1`. Bump on any breaking shape change. |
| `correlationId` | yes | UUIDv4 (no hyphens). Shared across caller + callee spans. |
| `callerAgentId` | yes | Caller agent id from `IAgentExecutionContext.AgentIdentity.Id`. |
| `callerKind` | yes | Caller identity kind, e.g. `FederatedCredential`. |
| `calleeAgentId` | yes | Target agent id; resolves DNS / DI key on the callee side. |
| `calleeSkill` | no | Target skill name. Null → callee's default skill. |
| `traceparent` | no | W3C trace-context header value, captured client-side. |
| `extensions` | no | Vendor-extensible headers, bounded by `MaxExtensionHeaders`. |

## Request / response

```json
// Request
{
  "envelope": { ... },
  "taskDescription": "Find open SEV-2 tickets for tenant contoso",
  "input": { "tenantId": "contoso", "severity": 2 }
}

// Success response
{
  "correlationId": "ec0b3a4d...",
  "success": true,
  "output": { "tickets": [ ... ] }
}

// Failure response
{
  "correlationId": "ec0b3a4d...",
  "success": false,
  "errorCode": "a2a.auth_rejected",
  "errorMessage": null
}
```

Stable failure codes:

| Code | Meaning |
| --- | --- |
| `a2a.auth_rejected` | Auth provider rejected the inbound envelope (no JWT, bad signature, sub mismatch, etc). |
| `a2a.unsupported_envelope_version` | `schemaVersion` does not match server's supported version. |
| `a2a.too_many_extensions` | Extension dictionary exceeded `MaxExtensionHeaders`. |
| `a2a.identity_conflict` | Server's execution scope was already bound to a different identity. |
| `a2a.skill_not_found` | No keyed handler registered for `{calleeAgentId}` or `{calleeAgentId}:{calleeSkill}`. |
| `a2a.skill_failed` | Handler threw or returned a `Result.Fail`. |
| `a2a.cancelled` | Cooperative cancellation. |
| `a2a.bad_response` | Cross-process callee returned an unparseable body. |
| `a2a.transport_failure` | HTTP-level error (DNS, TLS, connection reset). |
| `a2a.http_4xx` / `a2a.http_5xx` | HTTP error not covered above. |
| `a2a.unknown_callee` | No `RemoteAgents` entry matches the envelope's `calleeAgentId`. |
| `a2a.unknown_transport` | Configured `Transport` value is invalid. |
| `a2a.auth_acquisition_failed` | Client-side JWT acquirer failed. |

Server-side exceptions are logged via structured logging and never
returned on the wire — error messages are nullable and used only for
human-readable diagnostics when safe.

## Identity propagation

The caller stamps `callerAgentId` from
`IAgentExecutionContext.AgentIdentity.Id`. The server reads it, runs the
auth provider, and establishes the **authoritative** caller id on its
own `IAgentExecutionContext` via `A2AIdentityPropagator`:

- **In-process transport**: the envelope's declared caller id is
  authoritative because both ends share the same trust boundary. The
  auth provider also checks that the envelope's declared id matches the
  ambient identity to catch scope-leak bugs.
- **Cross-process transport**: the JWT `sub` claim is authoritative.
  If the envelope's declared `callerAgentId` does not match the JWT
  `sub`, the server returns `a2a.auth_rejected` — a holder of a token
  for agent A cannot masquerade as agent B in the envelope.

The propagator sets the caller's `AgentIdentity` on the server-side
scope so all downstream tool calls in the handler are RBAC'd against
the original caller, not a synthetic "callee" identity.

## OpenTelemetry span linking

Both sides emit spans on `ActivitySource = "AgenticHarness.A2A"`.

| Attribute | Caller span | Callee span |
| --- | --- | --- |
| Span name | `a2a.client {calleeAgentId}` | `a2a.server {calleeSkill | calleeAgentId}` |
| Kind | `Client` | `Server` |
| `gen_ai.operation.name` | `invoke_a2a` | `invoke_a2a` |
| `gen_ai.a2a.caller.id` | yes | yes |
| `gen_ai.a2a.caller.kind` | yes | yes |
| `gen_ai.a2a.callee.id` | yes | yes |
| `gen_ai.a2a.callee.skill` | optional | optional |
| `gen_ai.a2a.correlation_id` | yes | yes |
| `gen_ai.a2a.transport` | `in_process` or `http` | `in_process` or `http` |
| `gen_ai.a2a.auth.scheme` | `ambient` or `mtls+jwt` | `ambient` or `mtls+jwt` |
| `gen_ai.a2a.envelope.version` | yes | yes |
| `gen_ai.a2a.error.code` | on failure | on failure |
| `error.type` | mirrors error code | mirrors error code |

The correlation id appears on both spans so log search joins the two
ends even when the W3C trace-context propagator is broken or stripped
by an intermediary. For cross-process calls, the caller also stamps
`traceparent` onto the envelope; the server extracts it onto the
inbound span as a standard parent link.

## Authentication

### In-process (`A2ATransport.InProcess`)

- Scheme name: `ambient`.
- Trusts `IAgentExecutionContext.AgentIdentity` because the call never
  leaves the process boundary.
- Server-side: validates the envelope's declared caller matches the
  ambient identity (when one is set). A mismatch is a scope-leak bug
  and surfaces as `a2a.auth_rejected`.

### Cross-process (`A2ATransport.Http`)

- Scheme name: `mtls+jwt`.
- **Layer 1 — mutual TLS**: Kestrel terminates the connection with peer
  certificate authentication. Untrusted certs are rejected before any
  harness code runs. The harness expects Kestrel to be configured per
  the security playbook (cert thumbprint allowlist; revocation check
  enabled).
- **Layer 2 — workload identity JWT**: the client attaches
  `Authorization: Bearer <jwt>` via the consumer-supplied
  `IA2ATokenAcquirer`. The server validates via the
  consumer-supplied `IA2ATokenValidator`:
  - signature against the configured key material;
  - `iss` against `A2ASurfaceConfig.ExpectedIssuer`;
  - `aud` against `A2ASurfaceConfig.ExpectedAudience`;
  - `exp` with clock skew = `A2ASurfaceConfig.ClockSkewSeconds`
    (defaults to `0` — never widen without a recorded design decision);
  - revocation status against any configured revocation list.
- **Layer 3 — envelope match**: the JWT's `sub` claim must equal the
  envelope's declared `callerAgentId`. JWT sub is authoritative.

Why mTLS + JWT instead of JWT alone? mTLS pins the connection
**workload** (which Kestrel can verify cryptographically) and JWT pins
the **agent identity** running inside that workload (which the
identity provider issues). Separately validated, separately revocable.
The harness expresses this as two distinct trust layers rather than
folding identity into the connection cert.

## Transport selection

`AppConfig.AI.A2A.Surface.Transport`:

- `InProcess` (default): same-process dispatch via the in-process
  bridge — no HTTP, no JWT acquisition. Example demos and unit tests
  use this transport.
- `Http`: cross-process dispatch. Requires the consumer to register an
  `IA2ATokenAcquirer` and `IA2ATokenValidator` and to set
  `ExpectedAudience` + `ExpectedIssuer`.

`A2ASurfaceConfig.MaxExtensionHeaders` (default `16`) bounds the
extension dictionary the server will deserialize — protects against
malicious payload inflation.

## Skill handler registration

The server resolves a handler via keyed DI:

```csharp
// Default skill for the agent
services.AddKeyedScoped<IA2ASkillHandler, MyAgentHandler>("sre-agent");

// Named skill
services.AddKeyedScoped<IA2ASkillHandler, MySearchHandler>("workspace-agent:search-tickets");
```

`HarnessA2AServer` looks up the keyed handler in this precedence:

1. `{calleeAgentId}:{calleeSkill}` if `CalleeSkill` is non-null;
2. `{calleeAgentId}` otherwise.

Missing handlers surface as `a2a.skill_not_found`.

## Version pin

`Infrastructure.AI.Tests/A2A/A2AVersionPinTests` asserts:

- `Microsoft.Agents.AI` 1.9.0 does **not** expose a public A2A surface
  (canary: when MAF adds one, this test fails and the harness should
  switch to wrapping it).
- `A2AEnvelope.CurrentSchemaVersion == 1` — bumping requires updating
  the test and writing a migration note.

When MAF ships A2A primitives:

1. Failing test makes the situation visible.
2. Replace `HarnessA2AClient` / `HarnessA2AServer` with thin wrappers
   over the MAF types, keeping the harness `IA2AClient` / `IA2AServer`
   call-site shape unchanged.
3. Keep the envelope conventions — they encode harness-specific
   identity + correlation semantics that MAF's transport will not
   replicate.
