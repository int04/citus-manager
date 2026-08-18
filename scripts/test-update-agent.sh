#!/usr/bin/env sh
set -eu

suite_root="$(mktemp -d)"
agent_pid=""

stop_agent() {
    if [ -n "${agent_pid}" ]; then
        kill "${agent_pid}" 2>/dev/null || true
        wait "${agent_pid}" 2>/dev/null || true
        agent_pid=""
    fi
}

cleanup() {
    stop_agent
    rm -rf "${suite_root}"
}
trap cleanup EXIT HUP INT TERM

create_fake_docker() {
    case_root="$1"
    mkdir -p "${case_root}/bin"
    cat > "${case_root}/bin/docker" <<'EOF'
#!/usr/bin/env sh
printf '%s\n' "$*" >> "${FAKE_DOCKER_LOG}"
case "$*" in
    *"compose "*" ps -q app") printf 'app-container\n' ;;
    *"compose "*" exec -T postgres pg_dump"*)
        [ "${FAKE_SCENARIO}" != "backup_failure" ] || exit 1
        printf 'database-backup'
        ;;
    *"compose "*" exec -T app tar"*) printf 'keyring-backup' ;;
    *"compose "*" up -d --no-deps app") exit 0 ;;
    "pull ghcr.io/int04/citus-manager:"*)
        [ "${FAKE_SCENARIO}" != "pull_failure" ] || exit 1
        exit 0
        ;;
    *"io.citus-manager.update-protocol"*)
        if [ "${FAKE_SCENARIO}" = "protocol_mismatch" ]; then printf '2\n'; else printf '1\n'; fi
        ;;
    *"io.citus-manager.compose-generation"*) printf '1\n' ;;
    *"{{.Config.Image}}"*) printf 'ghcr.io/int04/citus-manager:26.08.17.1200\n' ;;
    *".State.Health"*)
        if [ "${FAKE_SCENARIO}" = "health_timeout" ]; then printf 'starting\n'; else printf 'healthy\n'; fi
        ;;
    *) printf 'Unexpected docker invocation: %s\n' "$*" >&2; exit 1 ;;
esac
EOF
    chmod 700 "${case_root}/bin/docker"
}

start_case() {
    case_name="$1"
    target_version="$2"
    case_root="${suite_root}/${case_name}"
    mkdir -p "${case_root}/state" "${case_root}/backups" "${case_root}/workspace"
    printf 'CITUS_MANAGER_DB_PASSWORD=test-only\nPRESERVED_SETTING=yes\n' > "${case_root}/workspace/.env"
    printf 'services: {}\n' > "${case_root}/workspace/compose.yaml"
    : > "${case_root}/docker.log"
    create_fake_docker "${case_root}"
    printf '{"requestId":"01234567-89ab-cdef-0123-456789abcdef","targetVersion":"%s"}\n' \
        "${target_version}" > "${case_root}/state/request.json"

    PATH="${case_root}/bin:${PATH}" \
    FAKE_SCENARIO="${case_name}" \
    FAKE_DOCKER_LOG="${case_root}/docker.log" \
    CITUS_MANAGER_COMPOSE_FILE="${case_root}/workspace/compose.yaml" \
    CITUS_MANAGER_ENV_FILE="${case_root}/workspace/.env" \
    CITUS_MANAGER_PROJECT_DIRECTORY="${case_root}/workspace" \
    CITUS_MANAGER_STATE_DIRECTORY="${case_root}/state" \
    CITUS_MANAGER_BACKUP_DIRECTORY="${case_root}/backups" \
    CITUS_MANAGER_LOOP_INTERVAL_SECONDS=1 \
    CITUS_MANAGER_HEALTH_POLL_SECONDS=1 \
    CITUS_MANAGER_HEALTH_TIMEOUT_SECONDS=1 \
    CITUS_MANAGER_HEARTBEAT_INTERVAL_SECONDS=1 \
        sh "$(dirname "$0")/update-agent.sh" &
    agent_pid=$!
}

wait_for_state() {
    case_name="$1"
    expected_state="$2"
    case_root="${suite_root}/${case_name}"
    attempt=0
    while [ "${attempt}" -lt 15 ]; do
        if grep -q "\"state\":\"${expected_state}\"" "${case_root}/state/status.json" 2>/dev/null; then
            stop_agent
            return 0
        fi
        attempt=$((attempt + 1))
        sleep 1
    done
    printf 'Case %s did not reach %s. Status: ' "${case_name}" "${expected_state}" >&2
    cat "${case_root}/state/status.json" 2>/dev/null >&2 || true
    return 1
}

assert_not_pinned() {
    case_name="$1"
    ! grep -q '^CITUS_MANAGER_IMAGE=' "${suite_root}/${case_name}/workspace/.env"
}

start_case invalid_tag latest
wait_for_state invalid_tag Failed
! grep -q '^pull ' "${suite_root}/invalid_tag/docker.log"
assert_not_pinned invalid_tag

start_case protocol_mismatch 26.08.18.1028
wait_for_state protocol_mismatch Failed
grep -q '^pull ghcr.io/int04/citus-manager:26.08.18.1028$' "${suite_root}/protocol_mismatch/docker.log"
! grep -q 'pg_dump' "${suite_root}/protocol_mismatch/docker.log"
assert_not_pinned protocol_mismatch

start_case pull_failure 26.08.18.1028
wait_for_state pull_failure Failed
grep -q '^pull ghcr.io/int04/citus-manager:26.08.18.1028$' "${suite_root}/pull_failure/docker.log"
assert_not_pinned pull_failure

start_case backup_failure 26.08.18.1028
wait_for_state backup_failure Failed
grep -q 'pg_dump' "${suite_root}/backup_failure/docker.log"
! grep -q 'exec -T app tar' "${suite_root}/backup_failure/docker.log"
! grep -q 'up -d --no-deps app' "${suite_root}/backup_failure/docker.log"
assert_not_pinned backup_failure

start_case health_timeout 26.08.18.1028
wait_for_state health_timeout Failed
grep -q 'up -d --no-deps app' "${suite_root}/health_timeout/docker.log"
grep -q '^CITUS_MANAGER_IMAGE=ghcr.io/int04/citus-manager:26.08.18.1028$' \
    "${suite_root}/health_timeout/workspace/.env"

success_root="${suite_root}/success"
mkdir -p "${success_root}/backups/11111111-1111-1111-1111-111111111111"
sleep 1
mkdir -p "${success_root}/backups/22222222-2222-2222-2222-222222222222"
sleep 1
mkdir -p "${success_root}/backups/33333333-3333-3333-3333-333333333333"
start_case success 26.08.18.1028
wait_for_state success Succeeded
grep -q '^pull ghcr.io/int04/citus-manager:26.08.18.1028$' "${success_root}/docker.log"
grep -q '^CITUS_MANAGER_IMAGE=ghcr.io/int04/citus-manager:26.08.18.1028$' "${success_root}/workspace/.env"
grep -q '^PRESERVED_SETTING=yes$' "${success_root}/workspace/.env"
test "$(find "${success_root}/workspace" -maxdepth 1 -name '.env.tmp.*' | wc -l | tr -d ' ')" -eq 0
test -s "${success_root}/backups/01234567-89ab-cdef-0123-456789abcdef/control-database.dump"
test -s "${success_root}/backups/01234567-89ab-cdef-0123-456789abcdef/data-protection-keys.tar.gz"
test ! -e "${success_root}/state/request.json"
test "$(find "${success_root}/backups" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ')" -eq 3
grep -Eq '^\{"protocol":1,"composeGeneration":1,"updatedAtUtc":"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z"\}$' \
    "${success_root}/state/updater-heartbeat.json"

printf 'update-agent test matrix passed\n'
