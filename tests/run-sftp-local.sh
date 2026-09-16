#!/usr/bin/env bash
set -euo pipefail

# Run the self-contained Linux verification artifact without an SDK or changes
# to ~/.ssh. The server is unprivileged, loopback-only, and stopped on exit.
runner="$(realpath "${1:?Usage: bash tests/run-sftp-local.sh /path/to/MySsh.Tests}")"
sshd_bin="$(command -v sshd || true)"
if [[ -z "$sshd_bin" && -x /usr/sbin/sshd ]]; then sshd_bin=/usr/sbin/sshd; fi
[[ -n "$sshd_bin" ]] || { echo 'OpenSSH server is required for this isolated test.' >&2; exit 1; }
test_dir="$(mktemp -d /tmp/my-ssh-sftp-check.XXXXXX)"
server_pid=''
cleanup() {
    result=$?
    if [[ -n "$server_pid" ]]; then
        kill "$server_pid" 2>/dev/null || true
        wait "$server_pid" 2>/dev/null || true
    fi
    if [[ "$result" != 0 && -f "$test_dir/sshd.log" ]]; then cat "$test_dir/sshd.log" >&2; fi
    # Only this script's mktemp directory is removed.
    if [[ "$test_dir" == /tmp/my-ssh-sftp-check.* ]]; then rm -rf -- "$test_dir"; fi
    exit "$result"
}
trap cleanup EXIT
port="${MYSSH_TEST_PORT:-22222}"
ssh-keygen -q -t ed25519 -N '' -f "$test_dir/host_key"
ssh-keygen -q -t ed25519 -N '' -f "$test_dir/client_key"
cp "$test_dir/client_key.pub" "$test_dir/authorized_keys"
mkdir "$test_dir/files" "$test_dir/bin"
cat > "$test_dir/sshd_config" <<EOF
Port $port
ListenAddress 127.0.0.1
HostKey $test_dir/host_key
PidFile $test_dir/sshd.pid
AuthorizedKeysFile $test_dir/authorized_keys
StrictModes no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
UsePAM no
AllowUsers $(id -un)
Subsystem sftp internal-sftp
EOF
"$sshd_bin" -t -f "$test_dir/sshd_config"
"$sshd_bin" -D -f "$test_dir/sshd_config" -E "$test_dir/sshd.log" &
server_pid=$!
awk -v port="$port" '{print "[127.0.0.1]:" port " " $1 " " $2}' "$test_dir/host_key.pub" > "$test_dir/known_hosts"
cat > "$test_dir/ssh_config" <<EOF
Host my-ssh-integration
    HostName 127.0.0.1
    Port $port
    User $(id -un)
    IdentityFile $test_dir/client_key
    IdentitiesOnly yes
    BatchMode yes
    ConnectTimeout 5
    StrictHostKeyChecking yes
    UserKnownHostsFile $test_dir/known_hosts
EOF
cat > "$test_dir/bin/ssh" <<'EOF'
#!/bin/sh
exec /usr/bin/ssh -F "$MYSSH_TEST_CONFIG" "$@"
EOF
chmod 700 "$test_dir/bin/ssh"
export MYSSH_TEST_CONFIG="$test_dir/ssh_config"
export PATH="$test_dir/bin:$PATH"
ready=false
for attempt in {1..20}; do
    kill -0 "$server_pid"
    if ssh my-ssh-integration true 2>/dev/null; then ready=true; break; fi
    sleep 0.1
done
[[ "$ready" == true ]] || { echo 'Isolated SSH server did not become ready.' >&2; exit 1; }
export MYSSH_TEST_HOST=my-ssh-integration
export MYSSH_TEST_USER="$(id -un)"
export MYSSH_TEST_ROOT="$test_dir/files"
"$runner"
