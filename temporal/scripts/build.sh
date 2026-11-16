#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p .build/site
uv export --frozen --no-dev --no-emit-project --output-file .build/requirements.txt >/dev/null
case "$(docker info --format '{{.Architecture}}')" in
  aarch64|arm64) platform=aarch64 ;;
  x86_64|amd64) platform=x86_64 ;;
  *) printf 'Unsupported Docker architecture.\n' >&2; exit 2 ;;
esac
uv pip install --target .build/site --python-platform "$platform-manylinux_2_28" \
  --python-version 3.13 --only-binary :all: --require-hashes \
  --requirement .build/requirements.txt
docker compose build worker
