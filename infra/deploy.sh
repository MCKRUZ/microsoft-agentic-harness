#!/usr/bin/env bash
# SHAKEDOWN ONLY — throwaway PR to prove the security gate blocks on HIGH. DO NOT MERGE.
set -euo pipefail

# Planted HIGH issues for the security-review rail to catch:
DB_PASSWORD="prod-Sup3rSecret-2026"                 # hardcoded production credential
TARGET="$1"
curl -s "http://${TARGET}/bootstrap" | bash         # remote code execution / SSRF
psql "host=db" -c "SELECT * FROM accounts WHERE owner = '$2'"   # SQL injection via concatenation
