\getenv application_password APPLICATION_PASSWORD
SELECT format('CREATE ROLE temporal_app LOGIN PASSWORD %L', :'application_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'temporal_app') \gexec
ALTER ROLE temporal_app PASSWORD :'application_password';
GRANT CONNECT, CREATE ON DATABASE temporal TO temporal_app;
GRANT CONNECT, CREATE ON DATABASE temporal_visibility TO temporal_app;
\connect temporal
GRANT USAGE, CREATE ON SCHEMA public TO temporal_app;
\connect temporal_visibility
GRANT USAGE, CREATE ON SCHEMA public TO temporal_app;
CREATE EXTENSION IF NOT EXISTS btree_gin;
SELECT current_database(), current_user, ssl, version
FROM pg_stat_ssl WHERE pid = pg_backend_pid();
