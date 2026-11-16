#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${REGISTRY_NAME:?Source env/azure.env first.}"
tag="approval-$(date -u +%Y%m%d%H%M%S)"
mkdir -p .build
uv export --frozen --no-dev --extra azure --no-emit-project --output-file .build/requirements-azure.txt >/dev/null
uv pip sync --target .build/site-amd64 --python-platform x86_64-manylinux_2_28 \
  --python-version 3.13 --only-binary :all: --require-hashes \
  .build/requirements-azure.txt
docker build --platform linux/amd64 --build-arg DEPENDENCY_DIRECTORY=.build/site-amd64 \
  --tag "temporal-approval-amd64:$tag" .
docker run --rm --platform linux/amd64 --entrypoint python "temporal-approval-amd64:$tag" \
  -c 'import approval.api, approval.workflows, temporalio; print("AMD64 application imports passed")'
az acr build --registry "$REGISTRY_NAME" --platform linux/amd64 \
  --image "temporal-approval:$tag" --build-arg DEPENDENCY_DIRECTORY=.build/site-amd64 \
  --file Dockerfile .
digest="$(az acr repository show --name "$REGISTRY_NAME" --image "temporal-approval:$tag" \
  --query digest --output tsv)"
login="$(az acr show --name "$REGISTRY_NAME" --query loginServer --output tsv)"
printf '%s/temporal-approval@%s\n' "$login" "$digest" > .build/azure-image.txt
printf 'Immutable image saved to temporal/.build/azure-image.txt\n'
