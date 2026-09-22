# AgentHub — self-hosted, non-Azure deployment

Runs `Presentation.AgentHub` headless (API + SignalR only, no web dashboard) with zero Azure
configuration — see issue #591 and
[`documentation/onboarding/18-self-hosted-docker.html`](../../documentation/onboarding/18-self-hosted-docker.html)
for the full walkthrough.

## Quick start

```bash
cp deploy/agenthub/.env.example deploy/agenthub/.env
# edit deploy/agenthub/.env with your provider API key

# from the repository root:
docker compose -f deploy/agenthub/docker-compose.yml --env-file deploy/agenthub/.env up --build
```

Verify:

```bash
curl http://localhost:8080/health/ai
curl http://localhost:8080/health/subsystems
```

## Building for a specific or multiple architectures

One Dockerfile builds both `linux/amd64` and `linux/arm64` (framework-dependent publish — no
per-architecture build steps needed):

```bash
docker buildx build --platform linux/amd64,linux/arm64 \
  -f src/Content/Presentation/Presentation.AgentHub/Dockerfile \
  -t agenthub:latest .
```

Build context is the repository root — see the Dockerfile's own header comment for why.

## What this does not include

- No Azure identity, Key Vault, or App Configuration — enforced by `DisableAzureConfigSources`
  (belt-and-braces: set both in the image's `ENV` and in `appsettings.Container.json`).
- No sign-in — `Auth:Disabled` + `Auth:AllowOutsideDevelopment` are both `true` in
  `appsettings.Container.json`. This is a deliberate trade-off for a deployment with no Entra
  tenant, not an oversight. See the docs page for how to add real authentication back.
- No telemetry export by default (`AppConfig:Observability:Exporters:Otlp:Enabled=false`) — no
  collector runs in this compose file. Uncomment the two OTLP lines in `docker-compose.yml` to
  point at one.
