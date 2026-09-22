# Self-hosted, non-Azure container for the agentic harness (issue #591). Runs Presentation.AgentHub
# headless — API + SignalR only, no Azure config required. Build for both architectures from a
# single Dockerfile (framework-dependent publish, no RuntimeIdentifier needed — the base image
# supplies the matching runtime per platform):
#
#   docker buildx build --platform linux/amd64,linux/arm64 \
#     -f src/Content/Presentation/Presentation.AgentHub/Dockerfile -t agenthub:latest .
#
# BUILD CONTEXT MUST BE THE REPOSITORY ROOT, not src/. Presentation.AgentHub.csproj pulls in
# skills/**/* and agents/**/* from four directories above itself — i.e. above src/ — so a src/-only
# context (the pattern Presentation.FoundryHost's Dockerfile uses, which has no such references)
# would silently publish an image with no skills and no agents: it builds and boots clean, then
# every agent-turn request fails to resolve an agent.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
# ASPNETCORE_URLS is IGNORED whenever Kestrel:Endpoints is set in appsettings.json (it is). BOTH
# endpoints must be overridden — a fresh container has no dev certificate, so Kestrel fails outright
# at startup with "Unable to configure HTTPS endpoint" unless the Https endpoint is also redirected
# to plain HTTP on a spare port (see appsettings.json's own Kestrel comment, issue #602).
ENV Kestrel__Endpoints__Http__Url=http://+:8080 \
    Kestrel__Endpoints__Https__Url=http://+:8081 \
    DisableAzureConfigSources=true \
    ASPNETCORE_ENVIRONMENT=Container

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build
COPY src/ src/
COPY skills/ skills/
COPY agents/ agents/
WORKDIR /build/src
RUN dotnet restore Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj
# -c Release is load-bearing, not just an optimization: it drops the two .esproj SPA
# ProjectReferences and Microsoft.AspNetCore.SpaProxy (Debug-only), so this build runs no npm and
# the published image is headless API + SignalR only, with no web dashboard.
RUN dotnet publish Content/Presentation/Presentation.AgentHub/Presentation.AgentHub.csproj \
    -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
# curl only — needed for docker-compose.yml's HEALTHCHECK against /health/ai. The base runtime
# image ships neither curl nor wget.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
# appsettings.Container.json redirects persistent state (conversations, planner/governance
# databases, logs) to appdata/ — relative to /app, not an absolute path, because
# GovernanceStatePaths.Resolve rejects AppConfig:AI:Governance:DurableState:DatabasePath outright
# if it resolves outside the application base directory (a deliberate path-traversal guard; proven
# the hard way — an earlier absolute /data path failed startup against this exact check). Mounted
# as a volume in compose at /app/appdata.
#
# The whole of /app is ALSO chowned: proven necessary by an actual container run during
# development — without appsettings.Container.json in place (e.g. a template consumer who forgot
# to copy it, or ASPNETCORE_ENVIRONMENT overridden away from "Container"), every path falls back to
# its default, relative to /app, and OwnerOnlyDirectoryHelper.Create's first attempt to create
# "logs" under a root-owned /app throws UnauthorizedAccessException before the host ever starts.
# Defaulting to a working (if less tidy) layout beats an opaque crash on a missing config file.
RUN mkdir -p /app/appdata && chown -R $APP_UID:$APP_UID /app
VOLUME /app/appdata
USER $APP_UID
ENTRYPOINT ["dotnet", "Presentation.AgentHub.dll"]
