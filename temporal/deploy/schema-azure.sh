#!/bin/sh
set -eu
for database in temporal temporal_visibility; do
  temporal-sql-tool --plugin postgres12 --ep "$POSTGRES_SEEDS" -u temporal_app -p 5432 \
    --tls --tls-ca-file /etc/ssl/certs/ca-certificates.crt --tls-server-name "$POSTGRES_SEEDS" \
    --db "$database" setup-schema -v 0.0
done
temporal-sql-tool --plugin postgres12 --ep "$POSTGRES_SEEDS" -u temporal_app -p 5432 \
  --tls --tls-ca-file /etc/ssl/certs/ca-certificates.crt --tls-server-name "$POSTGRES_SEEDS" \
  --db temporal update-schema -d /etc/temporal/schema/postgresql/v12/temporal/versioned
temporal-sql-tool --plugin postgres12 --ep "$POSTGRES_SEEDS" -u temporal_app -p 5432 \
  --tls --tls-ca-file /etc/ssl/certs/ca-certificates.crt --tls-server-name "$POSTGRES_SEEDS" \
  --db temporal_visibility update-schema -d /etc/temporal/schema/postgresql/v12/visibility/versioned
