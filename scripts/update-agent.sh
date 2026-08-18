#!/usr/bin/env sh
set -eu

COMPOSE_FILE="${CITUS_MANAGER_COMPOSE_FILE:-/workspace/compose.yaml}"
ENV_FILE="${CITUS_MANAGER_ENV_FILE:-/workspace/.env}"
PROJECT_DIRECTORY="${CITUS_MANAGER_PROJECT_DIRECTORY:-/workspace}"
STATE_DIRECTORY="${CITUS_MANAGER_STATE_DIRECTORY:-/var/lib/citus-manager/update}"
BACKUP_DIRECTORY="${CITUS_MANAGER_BACKUP_DIRECTORY:-/var/lib/citus-manager/update-backups}"
IMAGE_REPOSITORY="${CITUS_MANAGER_IMAGE_REPOSITORY:-ghcr.io/int04/citus-manager}"
UPDATE_PROTOCOL="${CITUS_MANAGER_UPDATE_PROTOCOL:-1}"
COMPOSE_GENERATION="${CITUS_MANAGER_COMPOSE_GENERATION:-1}"
HEALTH_TIMEOUT_SECONDS="${CITUS_MANAGER_HEALTH_TIMEOUT_SECONDS:-180}"
HEALTH_POLL_SECONDS="${CITUS_MANAGER_HEALTH_POLL_SECONDS:-5}"
LOOP_INTERVAL_SECONDS="${CITUS_MANAGER_LOOP_INTERVAL_SECONDS:-2}"
HEARTBEAT_INTERVAL_SECONDS="${CITUS_MANAGER_HEARTBEAT_INTERVAL_SECONDS:-2}"
REQUEST_FILE="${STATE_DIRECTORY}/request.json"
STATUS_FILE="${STATE_DIRECTORY}/status.json"
HEARTBEAT_FILE="${STATE_DIRECTORY}/updater-heartbeat.json"

case "${HEALTH_TIMEOUT_SECONDS}:${HEALTH_POLL_SECONDS}:${LOOP_INTERVAL_SECONDS}:${HEARTBEAT_INTERVAL_SECONDS}" in
    *[!0-9:]*) printf 'Updater timing settings must be non-negative integers.\n' >&2; exit 1 ;;
esac
case "${UPDATE_PROTOCOL}:${COMPOSE_GENERATION}" in
    *[!0-9:]*) printf 'Updater protocol and Compose generation must be non-negative integers.\n' >&2; exit 1 ;;
esac

umask 077
mkdir -p "${STATE_DIRECTORY}" "${BACKUP_DIRECTORY}"

write_heartbeat() {
    heartbeat_temporary="${HEARTBEAT_FILE}.tmp.$$"
    heartbeat_timestamp="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
    printf '{"protocol":%s,"composeGeneration":%s,"updatedAtUtc":"%s"}\n' \
        "${UPDATE_PROTOCOL}" "${COMPOSE_GENERATION}" "${heartbeat_timestamp}" > "${heartbeat_temporary}"
    chmod 644 "${heartbeat_temporary}"
    mv "${heartbeat_temporary}" "${HEARTBEAT_FILE}"
}

heartbeat_loop() {
    while :; do
        write_heartbeat
        sleep "${HEARTBEAT_INTERVAL_SECONDS}"
    done
}

heartbeat_pid=""
cleanup() {
    if [ -n "${heartbeat_pid}" ]; then
        kill "${heartbeat_pid}" 2>/dev/null || true
        wait "${heartbeat_pid}" 2>/dev/null || true
        heartbeat_pid=""
    fi
}
terminate() {
    cleanup
    exit 0
}
trap cleanup EXIT
trap terminate HUP INT TERM
heartbeat_loop &
heartbeat_pid=$!

json_value() {
    key="$1"
    file="$2"
    sed -n "s/.*\"${key}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "${file}" | head -n 1
}

write_status() {
    state="$1"
    message="$2"
    previous_image="${3:-}"
    temporary="${STATUS_FILE}.tmp.$$"
    timestamp="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
    # All interpolated values are constrained below to safe character sets.
    printf '{"requestId":"%s","targetVersion":"%s","previousImage":"%s","state":"%s","message":"%s","updatedAtUtc":"%s"}\n' \
        "${request_id}" "${target_version}" "${previous_image}" "${state}" "${message}" "${timestamp}" > "${temporary}"
    chmod 644 "${temporary}"
    mv "${temporary}" "${STATUS_FILE}"
}

fail_update() {
    message="$1"
    previous_image="${2:-}"
    write_status "Failed" "${message}" "${previous_image}"
    rm -f "${REQUEST_FILE}"
}

compose() {
    docker compose --project-directory "${PROJECT_DIRECTORY}" --file "${COMPOSE_FILE}" "$@"
}

retain_backups() {
    # Request IDs are GUIDs, so only validated direct children can be removed.
    count=0
    # BusyBox ls sorts these direct children by modification time.
    for path in $(ls -1dt "${BACKUP_DIRECTORY}"/* 2>/dev/null || true); do
        if [ -d "${path}" ]; then
            count=$((count + 1))
            if [ "${count}" -gt 3 ]; then
                case "${path}" in
                    "${BACKUP_DIRECTORY}"/*) rm -rf -- "${path}" ;;
                esac
            fi
        fi
    done
}

process_request() {
    request_id="$(json_value requestId "${REQUEST_FILE}")"
    target_version="$(json_value targetVersion "${REQUEST_FILE}")"

    case "${request_id}" in
        ????????-????-????-????-????????????) ;;
        *) request_id="invalid"; target_version=""; fail_update "Invalid request identifier"; return ;;
    esac
    case "${request_id}" in *[!0-9A-Fa-f-]*) fail_update "Invalid request identifier"; return ;; esac
    case "${target_version}" in
        [0-9][0-9].[0-9][0-9].[0-9][0-9].[0-9][0-9][0-9][0-9]) ;;
        *) fail_update "Invalid target version"; return ;;
    esac

    target_image="${IMAGE_REPOSITORY}:${target_version}"
    app_container_id="$(compose ps -q app 2>/dev/null || true)"
    previous_image="$(docker inspect --format '{{.Config.Image}}' "${app_container_id}" 2>/dev/null || true)"
    [ -n "${previous_image}" ] || previous_image="unknown"
    case "${previous_image}" in *[!A-Za-z0-9_./:@-]*) previous_image="unknown" ;; esac

    if [ ! -f "${ENV_FILE}" ]; then
        fail_update "Installer environment file is unavailable" "${previous_image}"
        return
    fi

    write_status "Pulling" "Pulling validated release" "${previous_image}"
    if ! docker pull "${target_image}" >/dev/null; then
        fail_update "Image pull failed" "${previous_image}"
        return
    fi

    protocol="$(docker image inspect --format '{{ index .Config.Labels "io.citus-manager.update-protocol" }}' "${target_image}" 2>/dev/null || true)"
    generation="$(docker image inspect --format '{{ index .Config.Labels "io.citus-manager.compose-generation" }}' "${target_image}" 2>/dev/null || true)"
    if [ "${protocol}" != "${UPDATE_PROTOCOL}" ] || [ "${generation}" != "${COMPOSE_GENERATION}" ]; then
        fail_update "Release requires a manual installer update" "${previous_image}"
        return
    fi

    write_status "BackingUp" "Backing up control database and keyring" "${previous_image}"
    request_backup="${BACKUP_DIRECTORY}/${request_id}"
    if ! mkdir -m 700 "${request_backup}"; then
        fail_update "Update backup directory already exists or cannot be created" "${previous_image}"
        return
    fi
    if ! compose exec -T postgres pg_dump --username=citus_manager --dbname=citus_manager --format=custom > "${request_backup}/control-database.dump"; then
        fail_update "Control database backup failed" "${previous_image}"
        return
    fi
    if [ ! -s "${request_backup}/control-database.dump" ]; then
        fail_update "Control database backup is empty" "${previous_image}"
        return
    fi
    if ! compose exec -T app tar -C /var/lib/citus-manager/keys -czf - . > "${request_backup}/data-protection-keys.tar.gz"; then
        fail_update "Data Protection key backup failed" "${previous_image}"
        return
    fi
    if [ ! -s "${request_backup}/data-protection-keys.tar.gz" ]; then
        fail_update "Data Protection key backup is empty" "${previous_image}"
        return
    fi
    printf '%s\n' "${previous_image}" > "${request_backup}/previous-image.txt"
    chmod 600 "${request_backup}"/*
    retain_backups

    env_temporary="${ENV_FILE}.tmp.$$"
    if ! awk -v image="${target_image}" '
        BEGIN { replaced=0 }
        /^CITUS_MANAGER_IMAGE=/ { print "CITUS_MANAGER_IMAGE=" image; replaced=1; next }
        { print }
        END { if (!replaced) print "CITUS_MANAGER_IMAGE=" image }
    ' "${ENV_FILE}" > "${env_temporary}"; then
        rm -f "${env_temporary}"
        fail_update "Installer environment file could not be updated" "${previous_image}"
        return
    fi
    if ! chmod 600 "${env_temporary}" || ! mv "${env_temporary}" "${ENV_FILE}"; then
        rm -f "${env_temporary}"
        fail_update "Installer environment file could not be replaced" "${previous_image}"
        return
    fi

    write_status "Restarting" "Restarting Citus Manager" "${previous_image}"
    if ! compose up -d --no-deps app >/dev/null; then
        fail_update "Application restart failed; automatic rollback was not attempted" "${previous_image}"
        return
    fi

    deadline=$(( $(date +%s) + HEALTH_TIMEOUT_SECONDS ))
    while [ "$(date +%s)" -lt "${deadline}" ]; do
        container_id="$(compose ps -q app 2>/dev/null || true)"
        if [ -n "${container_id}" ]; then
            health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "${container_id}" 2>/dev/null || true)"
            if [ "${health}" = "healthy" ]; then
                write_status "Succeeded" "Update completed" "${previous_image}"
                rm -f "${REQUEST_FILE}"
                return
            fi
        fi
        sleep "${HEALTH_POLL_SECONDS}"
    done

    fail_update "Application health check timed out; automatic rollback was not attempted" "${previous_image}"
}

while :; do
    if [ -f "${REQUEST_FILE}" ]; then
        request_id=""
        target_version=""
        process_request
    fi
    sleep "${LOOP_INTERVAL_SECONDS}"
done
