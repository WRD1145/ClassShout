#!/usr/bin/env bash
# Linux 开发机回归：真实局域网、中继、界面与签名保护；Windows 原生窗口另行验收。
set -euo pipefail
cd "$(dirname "$0")/.."
configuration="${CONFIGURATION:-Release}"
port="${CLASSSHOUT_TEST_PORT:-8097}"
dotnet build ClassShout.Linux.slnf -c "$configuration"
state="$(mktemp -d -t classshout-regress-XXXXXXXX)"
server_pid=''
cleanup() {
    if [[ -n "$server_pid" ]]; then
        kill "$server_pid" 2>/dev/null || true
        wait "$server_pid" 2>/dev/null || true
    fi
    rm -rf "$state"
}
trap cleanup EXIT
export CLASSSHOUT_CONFIG="$state/relay-config.json"
export CLASSSHOUT_USER_STATE="$state/relay-users.json"
export CLASSSHOUT_RELAY_STATE="$state/relay-state.json"
export CLASSSHOUT_BINDING_STATE="$state/relay-bindings.json"
export CLASSSHOUT_SCHEDULE_STATE="$state/relay-schedule.json"
export CLASSSHOUT_SCHEDULE_AUDIO="$state/relay-schedule-audio"
export CLASSSHOUT_LOG_DIR="$state/logs"
export CLASSSHOUT_SCHEDULE_TICK_MS=200
server="src/ClassShout.RelayServer/bin/$configuration/net10.0/ClassShout.RelayServer"
e2e="tools/ClassShout.EndToEnd/bin/$configuration/net10.0/ClassShout.EndToEnd"
preview="tools/ClassShout.DesignPreview/bin/$configuration/net10.0/ClassShout.DesignPreview"
expected_version="$(python -c 'import xml.etree.ElementTree as E; print(E.parse("Directory.Build.props").findtext(".//Version"))')"
"$preview" --version-selftest "$expected_version"
"$server" --urls "http://127.0.0.1:$port" > "$state/server.log" 2>&1 &
server_pid=$!
ready=false
for ((i=0; i<80; i++)); do
    if curl -fsS "http://127.0.0.1:$port/api/health" 2>/dev/null | python -c 'import json,sys; sys.exit(not json.load(sys.stdin).get("ok"))' 2>/dev/null; then
        ready=true
        break
    fi
    kill -0 "$server_pid" 2>/dev/null || break
    sleep 0.4
done
if [[ "$ready" != true ]]; then
    cat "$state/server.log"
    echo '中继启动失败。' >&2
    exit 1
fi
password="$(python -c 'import json,sys; print(json.load(open(sys.argv[1]))["AdminPassword"])' "$CLASSSHOUT_CONFIG")"
echo '运行局域网与中继回归…'
"$e2e"
"$e2e" --relay "http://127.0.0.1:$port" --admin-password "$password"
node scripts/smoke-webui.mjs
"$preview" --maintenance-selftest
"$preview" --plugin-management-selftest
pwsh -NoProfile -File scripts/test-release-signing.ps1
python scripts/smoke-font-startup.py
echo 'Linux 回归通过；Windows 托盘、窗口唤回与安装器须在 Windows 验收。'
