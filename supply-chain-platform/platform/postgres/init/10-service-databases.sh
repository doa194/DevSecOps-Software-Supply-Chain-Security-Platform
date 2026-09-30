#!/usr/bin/env bash
# Creates one database and one owning role per platform service on first start.
# Each service can only reach its own database, so a compromised Gitea cannot read
# Keycloak's users or the Control Plane's trust decisions.
set -euo pipefail

create_service_database() {
  local name="$1" password="$2"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<-SQL
    CREATE ROLE "$name" LOGIN PASSWORD '$password';
    CREATE DATABASE "$name" OWNER "$name";
    REVOKE ALL ON DATABASE "$name" FROM PUBLIC;
SQL
}

create_service_database gitea "$GITEA_DB_PASSWORD"
create_service_database keycloak "$KEYCLOAK_DB_PASSWORD"
create_service_database sonarqube "$SONARQUBE_DB_PASSWORD"
create_service_database controlplane "$CONTROLPLANE_DB_PASSWORD"
