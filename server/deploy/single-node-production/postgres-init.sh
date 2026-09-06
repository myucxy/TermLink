#!/bin/sh
set -eu

keycloak_password="$(cat /run/secrets/keycloak_db_password)"
case "$keycloak_password" in
  ''|*[!A-Za-z0-9_-]*)
    echo 'Invalid Keycloak database password.' >&2
    exit 1
    ;;
esac

psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  --set=keycloak_password="$keycloak_password" <<'SQL'
SELECT format('CREATE ROLE keycloak LOGIN PASSWORD %L', :'keycloak_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'keycloak') \gexec
SELECT format('ALTER ROLE keycloak LOGIN PASSWORD %L', :'keycloak_password') \gexec
SELECT 'CREATE DATABASE keycloak OWNER keycloak'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'keycloak') \gexec
SQL

unset keycloak_password
