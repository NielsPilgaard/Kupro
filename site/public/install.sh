#!/bin/sh
# Installs the kupro CLI: curl -fsSL https://kupro.app/install.sh | sh
# Downloads one binary from our EU bucket, checks it against SHA256SUMS and puts it in ~/.local/bin (ADR-018).
#
# KUPRO_VERSION       a version instead of the latest
# KUPRO_INSTALL_DIR   a folder instead of ~/.local/bin
# KUPRO_DOWNLOAD_URL  a mirror instead of our bucket
#
# Everything is in main, called on the last line, so a cut-off download runs nothing.

set -eu

fail() {
  echo "kupro install: $1" >&2
  exit 1
}

fetch() {
  if command -v curl > /dev/null 2>&1; then
    curl -fsSL "$1" -o "$2" || fail "can't download $1"
  elif command -v wget > /dev/null 2>&1; then
    wget -q "$1" -O "$2" || fail "can't download $1"
  else
    fail "needs curl or wget"
  fi
}

sha256() {
  if command -v sha256sum > /dev/null 2>&1; then
    sha256sum "$1" | cut -d ' ' -f 1
  elif command -v shasum > /dev/null 2>&1; then
    shasum -a 256 "$1" | cut -d ' ' -f 1
  else
    fail "needs sha256sum or shasum"
  fi
}

detect_rid() {
  os=$(uname -s)
  arch=$(uname -m)
  case "$os/$arch" in
    Darwin/arm64) echo osx-arm64 ;;
    Linux/x86_64 | Linux/amd64)
      if is_musl; then echo linux-musl-x64; else echo linux-x64; fi
      ;;
    Linux/aarch64 | Linux/arm64)
      if is_musl; then fail "no build for musl on arm64"; fi
      echo linux-arm64
      ;;
    *) fail "no build for $os on $arch. On Windows, use install.ps1" ;;
  esac
}

is_musl() {
  ls /lib/ld-musl-* > /dev/null 2>&1 && return 0
  ldd --version 2>&1 | grep -qi musl
}

main() {
  base=${KUPRO_DOWNLOAD_URL:-https://kupro-downloads.s3.gra.io.cloud.ovh.net}
  base=${base%/}
  dir=${KUPRO_INSTALL_DIR:-$HOME/.local/bin}
  rid=$(detect_rid)
  file="kupro-$rid"

  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT

  version=${KUPRO_VERSION:-}
  if [ -z "$version" ]; then
    fetch "$base/cli/latest" "$tmp/latest"
    version=$(tr -d '[:space:]' < "$tmp/latest")
  fi

  echo "Installing kupro $version ($rid)"
  fetch "$base/cli/$version/SHA256SUMS" "$tmp/SHA256SUMS"
  fetch "$base/cli/$version/$file" "$tmp/kupro"

  expected=$(awk -v f="$file" '$2 == f || $2 == "*" f { print $1 }' "$tmp/SHA256SUMS")
  [ -n "$expected" ] || fail "release $version has no checksum for $file"
  [ "$(sha256 "$tmp/kupro")" = "$expected" ] || fail "the download doesn't match its checksum; nothing was installed"

  mkdir -p "$dir"
  chmod 755 "$tmp/kupro"
  # A rename inside the folder, so a running kupro keeps working. mv across filesystems would copy over it instead.
  cp "$tmp/kupro" "$dir/.kupro.new"
  mv -f "$dir/.kupro.new" "$dir/kupro"

  echo "Installed $dir/kupro"
  case ":$PATH:" in
    *":$dir:"*) ;;
    *) echo "$dir isn't on your PATH. Add it to your shell profile: export PATH=\"$dir:\$PATH\"" ;;
  esac
  echo "Next step: kupro install mcp"
}

main "$@"
