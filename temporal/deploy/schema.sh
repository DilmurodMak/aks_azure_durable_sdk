#!/bin/sh
set -eu
for database in temporal temporal_visibility; do
  temporal-sql-tool --plugin postgres12 --ep postgres -u temporal -p 5432 \
    --db "$database" setup-schema -v 0.0
done
temporal-sql-tool --plugin postgres12 --ep postgres -u temporal -p 5432 \
  --db temporal update-schema -d /etc/temporal/schema/postgresql/v12/temporal/versioned
temporal-sql-tool --plugin postgres12 --ep postgres -u temporal -p 5432 \
  --db temporal_visibility update-schema -d /etc/temporal/schema/postgresql/v12/visibility/versioned
