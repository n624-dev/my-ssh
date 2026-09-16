#!/usr/bin/env bash
set -euo pipefail

# Dedicated loopback server and disposable keys on a GitHub-hosted runner only.
test_dir="$(mktemp -d "${RUNNER_TEMP:?}/my-ssh-sshd.XXXXXX")"
ssh-keygen -q -t ed25519 -N '' -f "$test_dir/host_key"
ssh-keygen -q -t ed25519 -N '' -f "$test_dir/client_key"
cp "$test_dir/client_key.pub" "$test_dir/authorized_keys"
mkdir "$test_dir/files"
cat > "$test_dir/sshd_config" <<EOF
Port 22222
ListenAddress 127.0.0.1
HostKey $test_dir/host_key
PidFile $test_dir/sshd.pid
AuthorizedKeysFile $test_dir/authorized_keys
StrictModes no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
UsePAM yes
AllowUsers $(id -un)
Subsystem sftp internal-sftp
EOF
sudo mkdir -p /run/sshd
sudo /usr/sbin/sshd -t -f "$test_dir/sshd_config"
sudo /usr/sbin/sshd -f "$test_dir/sshd_config" -E "$test_dir/sshd.log"
mkdir -p "$HOME/.ssh"
chmod 700 "$HOME/.ssh"
# Pin the generated host key; no host-key verification bypass.
awk '{print "[127.0.0.1]:22222 " $1 " " $2}' "$test_dir/host_key.pub" > "$test_dir/known_hosts"
cat >> "$HOME/.ssh/config" <<EOF

Host my-ssh-integration
    HostName 127.0.0.1
    Port 22222
    User $(id -un)
    IdentityFile $test_dir/client_key
    IdentitiesOnly yes
    BatchMode yes
    StrictHostKeyChecking yes
    UserKnownHostsFile $test_dir/known_hosts
EOF
chmod 600 "$HOME/.ssh/config"
ssh my-ssh-integration true
{
    echo "MYSSH_TEST_HOST=my-ssh-integration"
    echo "MYSSH_TEST_USER=$(id -un)"
    echo "MYSSH_TEST_ROOT=$test_dir/files"
    echo "MYSSH_SSHD_DIR=$test_dir"
} >> "$GITHUB_ENV"
