#!/usr/bin/env sh
set -eu

REPOSITORY_RAW_URL="https://raw.githubusercontent.com/int04/citus-manager/master"

fail() {
    printf 'Citus Manager installer: %s\n' "$1" >&2
    exit 1
}

if [ -n "${CITUS_MANAGER_INSTALL_DIR:-}" ]; then
    INSTALL_DIR="${CITUS_MANAGER_INSTALL_DIR}"
else
    [ -n "${HOME:-}" ] || fail "HOME is not set. Set CITUS_MANAGER_INSTALL_DIR to an absolute directory."
    INSTALL_DIR="${HOME}/citus-manager"
fi

COMPOSE_FILE="${INSTALL_DIR}/compose.yaml"
ENV_FILE="${INSTALL_DIR}/.env"
TEMP_COMPOSE="${COMPOSE_FILE}.tmp"
AGENT_FILE="${INSTALL_DIR}/scripts/update-agent.sh"
TEMP_AGENT="${AGENT_FILE}.tmp"

command -v docker >/dev/null 2>&1 || fail "Docker is required: https://docs.docker.com/get-docker/"
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is required."
command -v curl >/dev/null 2>&1 || fail "curl is required to download compose.yaml."

umask 077
mkdir -p "${INSTALL_DIR}/scripts" "${INSTALL_DIR}/update-backups"
trap 'rm -f "${TEMP_COMPOSE}" "${TEMP_AGENT}"' EXIT HUP INT TERM

curl --fail --show-error --silent --location \
    "${REPOSITORY_RAW_URL}/compose.yaml" \
    --output "${TEMP_COMPOSE}"
curl --fail --show-error --silent --location \
    "${REPOSITORY_RAW_URL}/scripts/update-agent.sh" \
    --output "${TEMP_AGENT}"
chmod 700 "${TEMP_AGENT}"

mv "${TEMP_COMPOSE}" "${COMPOSE_FILE}"
mv "${TEMP_AGENT}" "${AGENT_FILE}"

if [ -f "${ENV_FILE}" ]; then
    if grep -q '^CITUS_MANAGER_DB_PASSWORD=$' "${ENV_FILE}"; then
        fail "CITUS_MANAGER_DB_PASSWORD is empty in ${ENV_FILE}. Set it, then run this installer again."
    fi
fi

if ! grep -q '^CITUS_MANAGER_DB_PASSWORD=.' "${ENV_FILE}" 2>/dev/null; then
    PASSWORD="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
    printf 'CITUS_MANAGER_DB_PASSWORD=%s\n' "${PASSWORD}" >> "${ENV_FILE}"
    chmod 600 "${ENV_FILE}" 2>/dev/null || true
fi

docker compose --project-directory "${INSTALL_DIR}" --file "${COMPOSE_FILE}" up -d --pull always
docker compose --project-directory "${INSTALL_DIR}" --file "${COMPOSE_FILE}" ps

printf '\nCitus Manager installed in %s\n' "${INSTALL_DIR}"
printf 'Open http://localhost:2706/Account/Setup\n'
printf 'Manage it later with: cd "%s" && docker compose <command>\n' "${INSTALL_DIR}"
