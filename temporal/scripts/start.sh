#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
uv sync --frozen
if [ ! -f .env ]; then
  umask 077
  .venv/bin/python -c 'import secrets; from pathlib import Path; Path(".env").write_text("POSTGRES_PASSWORD=" + secrets.token_urlsafe(32) + "\n")'
fi
mkdir -p .data
chmod 700 .data
docker compose up --wait postgres server ui
uv run python -m approval.setup
bash scripts/build.sh
docker compose up --detach worker
docker compose up --detach console
for attempt in $(seq 1 60); do
  if curl --fail --silent http://127.0.0.1:5090/api/health >/dev/null; then
    printf 'Approval console: http://127.0.0.1:5090\nTemporal UI: http://127.0.0.1:8088\n'
    exit 0
  fi
  sleep 2
done
printf 'Console did not become ready; inspect docker compose logs console worker.\n' >&2
exit 1
