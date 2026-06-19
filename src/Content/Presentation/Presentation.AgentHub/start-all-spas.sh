#!/usr/bin/env bash
# Cross-platform companion to start-all-spas.cmd. Launches both Vite dev servers
# (Dashboard on :5174, WebUI on :5173) for the SpaProxy on macOS/Linux, where the
# Windows `cmd.exe /c start-all-spas.cmd` launch command cannot run.
#
# The SpaProxy invokes this with the AgentHub project directory as the working
# directory, but resolve paths from the script's own location so it also works when
# run by hand. The WebUI server runs in the foreground so this process stays alive
# for as long as SpaProxy expects the dev server to be up; the Dashboard server is
# backgrounded and torn down with it.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Tear down the backgrounded Dashboard server when this script exits.
cleanup() {
  if [[ -n "${dashboard_pid:-}" ]]; then
    kill "$dashboard_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

# Dashboard Vite (:5174) in the background.
(cd "$script_dir/../Presentation.Dashboard" && npm run dev) &
dashboard_pid=$!

# WebUI Vite (:5173) in the foreground — keeps the process alive for SpaProxy.
cd "$script_dir/../Presentation.WebUI"
exec npm run dev
