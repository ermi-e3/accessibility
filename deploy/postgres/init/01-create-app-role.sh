#!/bin/sh
set -eu

# This script runs only when PostgreSQL initializes an empty data volume.

psql -v ON_ERROR_STOP=1 \
  --username "$POSTGRES_USER" \
  --dbname "$POSTGRES_DB" \
  --set=migration_user="$ACCESSLEARN_MIGRATION_USER" \
  --set=migration_password="$ACCESSLEARN_MIGRATION_PASSWORD" \
  --set=app_user="$ACCESSLEARN_DB_USER" \
  --set=app_password="$ACCESSLEARN_DB_PASSWORD" <<-'EOSQL'
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'migration_user', :'migration_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'migration_user') \gexec
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L', :'app_user', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'app_user') \gexec
SELECT format('ALTER DATABASE %I OWNER TO %I', current_database(), :'migration_user') \gexec
SELECT format('ALTER SCHEMA public OWNER TO %I', :'migration_user') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'app_user') \gexec
SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'app_user') \gexec
SELECT format(
  'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I',
  :'migration_user', :'app_user') \gexec
SELECT format(
  'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %I',
  :'migration_user', :'app_user') \gexec
EOSQL
