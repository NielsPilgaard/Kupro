#!/usr/bin/env bash
# Hardens the Ubuntu (or Debian) VPS that runs Dokploy. Idempotent: run it again after any change.
# Run it on a new VPS before installing Dokploy, and on an existing one as it is.
# Host metrics need nothing here: the alloy container scrapes the host (infrastructure/observability).
#
#   scp infrastructure/vps/harden.sh ubuntu@<vps>:
#   ssh -t ubuntu@<vps> sudo bash harden.sh
#
# Settings (environment variables, all optional; pass them after sudo):
#   ADMIN_USER   The only user allowed to SSH in. Default: the user who ran sudo.
#   SSH_PUBKEY   A public key to add to ADMIN_USER's authorized_keys.
#   LOCK_SSH     auto (default): turn off password login only once ADMIN_USER has
#                logged in with a key in the last 7 days. yes: skip that check.
#                no: leave sshd alone.
#   TIMEZONE     Default Europe/Copenhagen, the backup agent's time zone.
#   SWAP_SIZE    Default 2G. 0 skips.
#   REBOOT_TIME  When unattended-upgrades reboots after a kernel update. Default
#                05:30, after the backup agent's 02:00 full, 03:00 verify, 04:00 drill.
#   REBOOT       yes: reboot at the end if one is pending. Default no.
#
# Docker-published ports bypass ufw. The script lists them at the end; close them
# in Dokploy, not here.

set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

ADMIN_USER=${ADMIN_USER:-${SUDO_USER:-}}
LOCK_SSH=${LOCK_SSH:-auto}
TIMEZONE=${TIMEZONE:-Europe/Copenhagen}
SWAP_SIZE=${SWAP_SIZE:-2G}
REBOOT_TIME=${REBOOT_TIME:-05:30}
REBOOT=${REBOOT:-no}

NOTES=()

step() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
log() { printf '   %s\n' "$*"; }
note() { NOTES+=("$*"); printf '   \033[33m! %s\033[0m\n' "$*"; }
die() { printf '\033[31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

# put PATH [MODE] < content: writes only when the content differs. Sets CHANGED=1/0.
put() {
	local path=$1 mode=${2:-644} tmp
	tmp=$(mktemp)
	cat >"$tmp"
	if [[ -f $path ]] && cmp -s "$tmp" "$path"; then
		CHANGED=0
		rm -f "$tmp"
		return
	fi
	install -D -m "$mode" "$tmp" "$path"
	rm -f "$tmp"
	CHANGED=1
	log "wrote $path"
}

preflight() {
	[[ $EUID -eq 0 ]] || die "Run as root: sudo bash $0"
	# shellcheck source=/dev/null
	. /etc/os-release
	[[ $ID == ubuntu || $ID == debian ]] || die "Written for Ubuntu and Debian, found $PRETTY_NAME"
	[[ -n $ADMIN_USER && $ADMIN_USER != root ]] || die "Set ADMIN_USER, or run through sudo as that user"
	id "$ADMIN_USER" >/dev/null 2>&1 || die "User $ADMIN_USER does not exist"
	log "$PRETTY_NAME, admin user $ADMIN_USER"
}

packages() {
	step "Packages"
	apt-get update -q
	apt-get install -y -q ufw unattended-upgrades jq curl ca-certificates >/dev/null
}

timezone() {
	step "Timezone"
	if [[ $(timedatectl show -p Timezone --value) != "$TIMEZONE" ]]; then
		timedatectl set-timezone "$TIMEZONE"
		log "set to $TIMEZONE"
	fi
}

swap() {
	step "Swap"
	if [[ $SWAP_SIZE == 0 ]]; then
		log "skipped"
	elif [[ -n $(swapon --show --noheadings) ]]; then
		log "already on"
	else
		# Recreate a swapfile without a swap header, e.g. from a run stopped before mkswap.
		if [[ $(blkid -p -s TYPE -o value /swapfile 2>/dev/null) != swap ]]; then
			rm -f /swapfile
			fallocate -l "$SWAP_SIZE" /swapfile
			chmod 600 /swapfile
			mkswap /swapfile >/dev/null
		fi
		swapon /swapfile
		grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >>/etc/fstab
		log "$SWAP_SIZE swapfile on"
	fi
	# Swap is a safety net against OOM kills during builds, not extra RAM.
	put /etc/sysctl.d/90-swappiness.conf <<<'vm.swappiness = 10'
	((CHANGED)) && sysctl -q -p /etc/sysctl.d/90-swappiness.conf
	return 0
}

journald() {
	step "Journal size"
	put /etc/systemd/journald.conf.d/00-size.conf <<-'EOF'
		[Journal]
		SystemMaxUse=500M
	EOF
	if ((CHANGED)); then
		systemctl restart systemd-journald
		journalctl --vacuum-size=500M -q
	fi
}

no_llmnr() {
	step "LLMNR/mDNS off"
	if ! systemctl is-active --quiet systemd-resolved; then
		log "systemd-resolved not running, nothing to do"
		return
	fi
	put /etc/systemd/resolved.conf.d/00-no-llmnr.conf <<-'EOF'
		[Resolve]
		LLMNR=no
		MulticastDNS=no
	EOF
	((CHANGED)) && systemctl restart systemd-resolved
	return 0
}

auto_upgrades() {
	step "Unattended upgrades + nightly reboot"
	put /etc/apt/apt.conf.d/20auto-upgrades <<-'EOF'
		APT::Periodic::Update-Package-Lists "1";
		APT::Periodic::Unattended-Upgrade "1";
	EOF
	# Kernel fixes only take effect after a reboot. The default origins are the distro's
	# security updates only, so Docker is never upgraded unattended.
	put /etc/apt/apt.conf.d/52reboot <<-EOF
		Unattended-Upgrade::Automatic-Reboot "true";
		Unattended-Upgrade::Automatic-Reboot-WithUsers "true";
		Unattended-Upgrade::Automatic-Reboot-Time "$REBOOT_TIME";
		Unattended-Upgrade::Remove-Unused-Kernel-Packages "true";
	EOF
	systemctl enable --now unattended-upgrades >/dev/null 2>&1
}

docker_logs() {
	step "Docker log rotation"
	local want='{"log-driver":"json-file","log-opts":{"max-size":"20m","max-file":"3"}}' merged
	if [[ -s /etc/docker/daemon.json ]]; then
		# Another driver's log-opts (syslog-address, ...) would stop Docker from starting
		# under json-file, so drop them when the driver changes.
		merged=$(jq --argjson w "$want" 'if has("log-driver") and .["log-driver"] != $w["log-driver"] then del(.["log-opts"]) else . end | . * $w' /etc/docker/daemon.json) ||
			die "/etc/docker/daemon.json is not valid JSON"
	else
		merged=$(jq . <<<"$want")
	fi
	# No live-restore: it conflicts with Swarm, which Dokploy uses.
	put /etc/docker/daemon.json <<<"$merged"
	if ((CHANGED)) && docker_restart_pending; then
		note "Docker log limits apply after Docker restarts (the reboot does it) and only to containers created after that"
	fi
}

# Docker reads daemon.json when it starts, so a change is pending until Docker has
# started after the file's last write. Checked each run, so a rerun still knows.
docker_restart_pending() {
	[[ -f /etc/docker/daemon.json ]] && systemctl is-active --quiet docker || return 1
	local started
	started=$(systemctl show docker.service -p ActiveEnterTimestamp --value --timestamp=unix 2>/dev/null || true)
	started=${started#@}
	[[ $started =~ ^[0-9]+$ ]] || return 1
	(($(stat -c %Y /etc/docker/daemon.json) > started))
}

docker_prune() {
	step "Weekly Docker image prune"
	# Images and build cache only. Never volumes: the spare kupro-pgdata volume is unused
	# on purpose (docs/runbooks/restore.md).
	put /etc/systemd/system/docker-prune.service <<-'EOF'
		[Unit]
		Description=Remove Docker images and build cache unused for 7 days (never volumes)
		ConditionPathExists=/usr/bin/docker

		[Service]
		Type=oneshot
		ExecStart=/usr/bin/docker image prune --all --force --filter until=168h
		ExecStart=/usr/bin/docker builder prune --all --force --filter until=168h
	EOF
	put /etc/systemd/system/docker-prune.timer <<-'EOF'
		[Unit]
		Description=Weekly Docker image prune

		[Timer]
		OnCalendar=Sun 06:30
		Persistent=true

		[Install]
		WantedBy=timers.target
	EOF
	systemctl daemon-reload
	systemctl enable --now docker-prune.timer >/dev/null 2>&1
}

firewall() {
	step "ufw"
	# Allow the port sshd really listens on before turning ufw on, or a VPS with
	# SSH on another port locks us out. On Ubuntu 24.04+ ssh.socket listens, and a
	# generator derives its port from the same sshd_config, so sshd -T still holds.
	local ssh_ports
	# "listenaddress addr:port" too: a ListenAddress with its own port overrides Port, and
	# sshd -T still prints "port 22" then.
	ssh_ports=$(sshd -T 2>/dev/null | awk '$1 == "port" {print $2} $1 == "listenaddress" {n = split($2, a, ":"); print a[n]}' |
		grep -xE '[0-9]+' | sort -un | tr '\n' ' ' || true)
	[[ -n $ssh_ports ]] || die "Could not read the SSH port from 'sshd -T'. ufw left as it was."
	for port in $ssh_ports; do
		ufw limit "$port/tcp" comment ssh >/dev/null
	done
	ufw default deny incoming >/dev/null
	ufw default allow outgoing >/dev/null
	ufw allow 80/tcp comment http >/dev/null
	ufw allow 443/tcp comment https >/dev/null
	ufw allow 443/udp comment http3 >/dev/null
	ufw --force enable >/dev/null
	log "deny incoming except SSH ${ssh_ports% } (rate-limited), 80, 443"
}

ssh_hardening() {
	step "SSH"
	local home ak keys
	home=$(getent passwd "$ADMIN_USER" | cut -d: -f6)
	ak=$home/.ssh/authorized_keys

	if [[ -n ${SSH_PUBKEY:-} ]]; then
		ssh-keygen -lf - <<<"$SSH_PUBKEY" >/dev/null 2>&1 || die "SSH_PUBKEY is not a valid public key"
		install -d -m 700 -o "$ADMIN_USER" -g "$(id -gn "$ADMIN_USER")" "$home/.ssh"
		touch "$ak"
		grep -qxF "$SSH_PUBKEY" "$ak" || { echo "$SSH_PUBKEY" >>"$ak" && log "added key to $ak"; }
		chown "$ADMIN_USER:" "$ak"
		chmod 600 "$ak"
	fi

	keys=$(ssh-keygen -lf "$ak" 2>/dev/null | wc -l || true)
	case $LOCK_SSH in
	no)
		log "LOCK_SSH=no, sshd left alone"
		return
		;;
	yes)
		((keys > 0)) || die "$ak has no valid key. Refusing to turn off password login."
		;;
	auto)
		if ((keys == 0)); then
			note "SSH password login still ON: $ADMIN_USER has no key in $ak"
			return
		fi
		# grep without -q: an early exit would SIGPIPE journalctl and fail the pipeline.
		if ! journalctl -u ssh.service --since -7d -q 2>/dev/null | grep -F "Accepted publickey for $ADMIN_USER " >/dev/null; then
			note "SSH password login still ON: no key login by $ADMIN_USER in the last 7 days. Log in with your key, then run again."
			return
		fi
		;;
	*) die "LOCK_SSH must be auto, yes or no" ;;
	esac

	# 00- so it wins: sshd keeps the first value it reads, and 50-cloud-init.conf
	# turns PasswordAuthentication on. TCP forwarding stays on for the SSH tunnels
	# (backup console, Dokploy panel).
	put /etc/ssh/sshd_config.d/00-hardening.conf <<-EOF
		# Managed by infrastructure/vps/harden.sh
		PasswordAuthentication no
		KbdInteractiveAuthentication no
		PubkeyAuthentication yes
		PermitRootLogin no
		AllowUsers $ADMIN_USER
		MaxAuthTries 3
		LoginGraceTime 30
		X11Forwarding no
	EOF
	if ! sshd -t; then
		rm -f /etc/ssh/sshd_config.d/00-hardening.conf
		die "sshd config invalid, drop-in removed, nothing changed"
	fi
	# Captured first: piping sshd -T into grep -q fails under pipefail, because
	# grep exits on the match and sshd dies of SIGPIPE writing the rest.
	local effective
	effective=$(sshd -T) || die "sshd -T failed"
	if ! grep -qx 'passwordauthentication no' <<<"$effective"; then
		rm -f /etc/ssh/sshd_config.d/00-hardening.conf
		die "sshd still allows passwords. Set in: $(grep -rilE '^[[:space:]]*passwordauthentication' /etc/ssh/sshd_config /etc/ssh/sshd_config.d | tr '\n' ' ')Drop-in removed, nothing changed."
	fi
	# Reload even when the drop-in was already there: an earlier run may have
	# written it and stopped before reloading. Open sessions stay open. Under
	# socket activation (Ubuntu 24.04+) ssh.service may not run yet; the next
	# connection starts it with the new config.
	systemctl try-reload-or-restart ssh.service
	note "Password login is OFF. Keep this session open and test 'ssh $ADMIN_USER@<vps>' in a new terminal."
}

audit() {
	step "Audit"
	if command -v docker >/dev/null; then
		local open
		open=$({ docker ps --format '{{.Ports}}' && docker service ls --format '{{.Ports}}' 2>/dev/null; } |
			grep -oE '(0\.0\.0\.0|\[::\]|\*):[0-9]+' | grep -oE '[0-9]+$' | sort -un | grep -vxE '80|443' | tr '\n' ' ' || true)
		[[ -n $open ]] && note "Docker publishes these ports to the internet, ufw can't block them: $open"
	fi
	if [[ -f /run/reboot-required ]]; then
		note "Reboot pending: package updates (running kernel $(uname -r))"
	fi
	if docker_restart_pending; then
		note "Reboot pending: Docker has not loaded /etc/docker/daemon.json yet"
	fi
	return 0
}

preflight
packages
timezone
swap
journald
no_llmnr
auto_upgrades
docker_logs
docker_prune
firewall
ssh_hardening
audit

step "Done"
for n in "${NOTES[@]}"; do log "- $n"; done
if [[ $REBOOT == yes ]] && { [[ -f /run/reboot-required ]] || docker_restart_pending; }; then
	log "Rebooting in 1 minute (shutdown -c cancels)"
	shutdown -r +1
fi
