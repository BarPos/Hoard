#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
#  Hoard installer · macOS on Apple Silicon
#
#    curl -fsSL https://raw.githubusercontent.com/BarPos/Hoard/main/install.sh | bash
#
#  Run without arguments for an interactive menu, or use a command:
#
#    install.sh install   [--version X.Y.Z] [--user | --system] [--yes]
#    install.sh update    [--yes]
#    install.sh uninstall [--user | --system] [--purge] [--yes]
#    install.sh list      (available versions)
#    install.sh status    (what is installed where)
#
#  --user    installs to ~/.local/bin (no admin rights needed)
#  --system  installs to /usr/local/bin for all users (asks for your password via sudo)
#  --purge   with uninstall: also remove settings and data (~/.config/hoard, ~/.local/share/hoard)
#  --yes     never ask; use defaults (user install, latest version)
#
#  Environment: HOARD_REPO (default BarPos/Hoard), NO_COLOR.
#  For testing: HOARD_RELEASES_DIR=/path serves releases from <dir>/v<version>/ instead of GitHub,
#  and HOARD_USER_DIR / HOARD_SYSTEM_DIR override the two install folders.
#  Works with the stock macOS bash 3.2.
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

REPO="${HOARD_REPO:-BarPos/Hoard}"
RELEASES_DIR="${HOARD_RELEASES_DIR:-}"
USER_DIR="${HOARD_USER_DIR:-${HOME}/.local/bin}"
SYSTEM_DIR="${HOARD_SYSTEM_DIR:-/usr/local/bin}"
CONFIG_DIRS=("${HOME}/.config/hoard" "${HOME}/.local/share/hoard")

# ── output ──────────────────────────────────────────────────────────────────
if [ -t 2 ] && [ -z "${NO_COLOR:-}" ]; then
  C_ACC=$'\e[38;5;214m'; C_OK=$'\e[38;5;114m'; C_WARN=$'\e[38;5;221m'; C_BAD=$'\e[38;5;203m'
  C_MUT=$'\e[38;5;244m'; C_B=$'\e[1m'; C_0=$'\e[0m'
else
  C_ACC=""; C_OK=""; C_WARN=""; C_BAD=""; C_MUT=""; C_B=""; C_0=""
fi
say()  { printf '%s\n' "$*" >&2; }
info() { say "${C_ACC}▸${C_0} $*"; }
ok()   { say "${C_OK}✓${C_0} $*"; }
warn() { say "${C_WARN}!${C_0} $*"; }
die()  { say "${C_BAD}✗${C_0} $*"; exit 1; }

banner() {
  say ""
  say "  ${C_B}${C_ACC}◆ HOARD${C_0}  ${C_MUT}what's hoarding your disk? · installer${C_0}"
  say ""
}

# ── input (works when the script itself is piped into bash) ─────────────────
ASSUME_YES=0
TTY=""
if [ -r /dev/tty ] && ( : < /dev/tty ) 2>/dev/null; then TTY=/dev/tty; fi

# ask "question" "default" → REPLY
ask() {
  local q="$1" def="${2:-}"
  if [ "$ASSUME_YES" = 1 ] || [ -z "$TTY" ]; then
    [ "$ASSUME_YES" = 1 ] || [ -n "$def" ] || die "no terminal to ask '$q'; pass options (see --help) or --yes"
    REPLY="$def"
    return
  fi
  if [ -n "$def" ]; then printf '%s %s[%s]%s ' "$q" "$C_MUT" "$def" "$C_0" >&2; else printf '%s ' "$q" >&2; fi
  IFS= read -r REPLY < "$TTY" || REPLY=""
  [ -n "$REPLY" ] || REPLY="$def"
}

confirm() { # confirm "question" "Y|N" → 0 for yes
  local def="${2:-Y}" hint="[Y/n]"
  [ "$def" = N ] && hint="[y/N]"
  if [ "$ASSUME_YES" = 1 ]; then return 0; fi
  if [ -z "$TTY" ]; then [ "$def" = Y ]; return; fi
  printf '%s %s%s%s ' "$1" "$C_MUT" "$hint" "$C_0" >&2
  local a; IFS= read -r a < "$TTY" || a=""
  [ -n "$a" ] || a="$def"
  case "$a" in [Yy]*) return 0 ;; *) return 1 ;; esac
}

# ── platform ────────────────────────────────────────────────────────────────
check_platform() {
  [ "$(uname -s)" = Darwin ] || die "Hoard currently supports macOS only (this is $(uname -s))."
  local arch; arch="$(uname -m)"
  # A shell running under Rosetta reports x86_64 on an Apple Silicon Mac.
  if [ "$arch" != arm64 ] && [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" != 1 ]; then
    die "Hoard currently supports Apple Silicon Macs only (this Mac is $arch)."
  fi
  command -v curl >/dev/null || [ -n "$RELEASES_DIR" ] || die "curl is required"
  command -v shasum >/dev/null || die "shasum is required"
}

# ── versions ────────────────────────────────────────────────────────────────
valid_version() { [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; }

# version_lt A B → 0 if A < B (numeric x.y.z; a pre-release sorts before its release)
version_lt() {
  [ "$1" = "$2" ] && return 1
  awk -v a="$1" -v b="$2" 'BEGIN {
    na = split(a, x, /[.-]/); nb = split(b, y, /[.-]/)
    for (i = 1; i <= 3; i++) { if (x[i] + 0 < y[i] + 0) exit 0; if (x[i] + 0 > y[i] + 0) exit 1 }
    pa = (index(a, "-") > 0); pb = (index(b, "-") > 0)
    if (pa && !pb) exit 0; if (!pa && pb) exit 1
    exit (a < b) ? 0 : 1 }'
}

# Never fails: on any HTTP or network error it prints nothing, and callers explain what's missing.
api_get() { curl -fsSL -H "Accept: application/vnd.github+json" "https://api.github.com/repos/${REPO}/$1" 2>/dev/null || true; }

# Prints available versions, newest first (without the leading "v").
available_versions() {
  if [ -n "$RELEASES_DIR" ]; then
    ls -1 "$RELEASES_DIR" 2>/dev/null | sed -n 's/^v//p' | awk '{print}' | sort -t. -k1,1nr -k2,2nr -k3,3nr
    return
  fi
  api_get "releases?per_page=50" | sed -n 's/^ *"tag_name": *"v\{0,1\}\([^"]*\)".*/\1/p' || true
}

latest_version() {
  if [ -n "$RELEASES_DIR" ]; then
    available_versions | grep -v -- - | head -1 || true
    return
  fi
  api_get "releases/latest" | sed -n 's/^ *"tag_name": *"v\{0,1\}\([^"]*\)".*/\1/p' | head -1 || true
}

# ── installations ───────────────────────────────────────────────────────────
installed_version() { # installed_version /path/to/hoard → version or empty
  [ -x "$1" ] || return 0
  "$1" --version 2>/dev/null | head -1 || true
}

dir_for_scope() { if [ "$1" = system ]; then echo "$SYSTEM_DIR"; else echo "$USER_DIR"; fi; }

run_in() { # run_in DIR cmd... (uses sudo when DIR isn't writable)
  local dir="$1"; shift
  local probe="$dir"
  while [ ! -d "$probe" ]; do probe="$(dirname "$probe")"; done
  if [ -w "$probe" ]; then "$@"; else
    info "administrator rights are needed for $dir"
    sudo "$@"
  fi
}

status() {
  local found=0 v
  for scope in user system; do
    local dir; dir="$(dir_for_scope "$scope")"
    v="$(installed_version "$dir/hoard")"
    if [ -n "$v" ]; then
      found=1
      ok "Hoard ${C_B}$v${C_0} installed for ${scope} in $dir"
    fi
  done
  local onpath; onpath="$(command -v hoard 2>/dev/null || true)"
  if [ -n "$onpath" ]; then say "  ${C_MUT}'hoard' on your PATH runs ${onpath}${C_0}"; fi
  [ "$found" = 1 ] || say "  Hoard is not installed in $USER_DIR or $SYSTEM_DIR."
  return 0
}

# ── download + install ──────────────────────────────────────────────────────
TMP=""
cleanup() { [ -n "$TMP" ] && rm -rf "$TMP"; return 0; }
trap cleanup EXIT

fetch() { # fetch URL-or-path DEST
  if [ -n "$RELEASES_DIR" ]; then cp "$1" "$2"; else curl -fL --progress-bar -o "$2" "$1"; fi
}

download_and_verify() { # download_and_verify VERSION → sets STAGED (path to verified binary)
  local v="$1" asset="hoard-$1-macos-arm64.tar.gz" base
  if [ -n "$RELEASES_DIR" ]; then base="$RELEASES_DIR/v$v"; else base="https://github.com/${REPO}/releases/download/v$v"; fi
  TMP="$(mktemp -d)"
  info "downloading Hoard $v"
  fetch "$base/$asset" "$TMP/$asset" || die "could not download $asset (does version $v exist? try: install.sh list)"
  fetch "$base/$asset.sha256" "$TMP/$asset.sha256" || die "could not download the checksum for $asset"
  ( cd "$TMP" && shasum -a 256 -c "$asset.sha256" >/dev/null 2>&1 ) || die "checksum mismatch: the download is damaged or was tampered with"
  ok "checksum verified"
  tar -xzf "$TMP/$asset" -C "$TMP"
  STAGED="$TMP/hoard-$v-macos-arm64/hoard"
  [ -x "$STAGED" ] || die "the archive doesn't contain the hoard binary"
  xattr -d com.apple.quarantine "$STAGED" 2>/dev/null || true
  local reported; reported="$("$STAGED" --version 2>/dev/null || true)"
  [ "$reported" = "$v" ] || die "the downloaded binary reports version '$reported', expected $v"
}

ensure_on_path() { # ensure_on_path DIR
  local dir="$1"
  case ":$PATH:" in *":$dir:"*) return 0 ;; esac
  warn "$dir is not on your PATH"
  local rc
  case "${SHELL##*/}" in
    zsh) rc="$HOME/.zshrc" ;;
    bash) rc="$HOME/.bash_profile" ;;
    fish) rc="$HOME/.config/fish/config.fish" ;;
    *) rc="$HOME/.profile" ;;
  esac
  local line="export PATH=\"$dir:\$PATH\""
  [ "${SHELL##*/}" = fish ] && line="fish_add_path $dir"
  if grep -qsF "$line" "$rc"; then
    say "  ${C_MUT}$rc already adds it; open a new terminal window.${C_0}"
  elif confirm "  Add it to $rc?" Y; then
    mkdir -p "$(dirname "$rc")"
    printf '\n# Added by the Hoard installer\n%s\n' "$line" >> "$rc"
    ok "added to $rc · open a new terminal window (or run: source $rc)"
  else
    say "  Add this line to your shell profile yourself:  $line"
  fi
}

install_version() { # install_version VERSION SCOPE
  local v="$1" scope="$2" dir; dir="$(dir_for_scope "$2")"
  local current; current="$(installed_version "$dir/hoard")"
  if [ "$current" = "$v" ] && ! confirm "Hoard $v is already installed in $dir. Reinstall?" N; then
    say "  Nothing to do."
    return 0
  fi
  download_and_verify "$v"
  run_in "$dir" mkdir -p "$dir"
  # Copy then rename, so a running hoard is never half-overwritten.
  run_in "$dir" cp "$STAGED" "$dir/.hoard.new"
  run_in "$dir" chmod 755 "$dir/.hoard.new"
  run_in "$dir" mv -f "$dir/.hoard.new" "$dir/hoard"
  if [ -n "$current" ] && [ "$current" != "$v" ]; then
    ok "Hoard ${C_B}$current → $v${C_0} in $dir"
  else
    ok "Hoard ${C_B}$v${C_0} installed in $dir"
  fi
  [ "$scope" = user ] && ensure_on_path "$dir"
  local other; other="$(dir_for_scope "$([ "$scope" = user ] && echo system || echo user)")"
  if [ -n "$(installed_version "$other/hoard")" ]; then
    warn "another copy ($(installed_version "$other/hoard")) is installed in $other; whichever comes first on your PATH runs"
  fi
  say "  Run ${C_B}hoard${C_0} in a folder, or ${C_B}hoard ~${C_0} for your home folder."
}

choose_scope() { # → SCOPE  (--yes without --user/--system means a per-user install)
  if [ -n "${SCOPE:-}" ]; then return; fi
  if [ "$ASSUME_YES" = 1 ]; then SCOPE=user; return; fi
  say ""
  say "  Install for:"
  say "    1) just me       ${C_MUT}$USER_DIR · no password needed${C_0}"
  say "    2) all users     ${C_MUT}$SYSTEM_DIR · asks for your password${C_0}"
  ask "  Choose 1 or 2:" 1
  case "$REPLY" in 2|system|s) SCOPE=system ;; *) SCOPE=user ;; esac
}

choose_version() { # → VERSION
  local versions latest
  versions="$(available_versions)"
  [ -n "$versions" ] || die "no releases found for $REPO"
  latest="$(latest_version)"; [ -n "$latest" ] || latest="$(echo "$versions" | head -1)"
  say ""
  say "  Available versions:"
  local i=1
  while IFS= read -r v; do
    [ -n "$v" ] || continue
    local tag=""; [ "$v" = "$latest" ] && tag=" ${C_OK}(latest)${C_0}"
    say "    $i) $v$tag"
    i=$((i + 1))
    [ "$i" -gt 15 ] && break
  done <<< "$versions"
  ask "  Version number or list position:" "$latest"
  if [[ "$REPLY" =~ ^[0-9]+$ ]] && [ "$REPLY" -ge 1 ] && [ "$REPLY" -lt "$i" ]; then
    VERSION="$(echo "$versions" | sed -n "${REPLY}p")"
  else
    VERSION="${REPLY#v}"
  fi
  valid_version "$VERSION" || die "'$VERSION' is not a version number"
  echo "$versions" | grep -qxF "$VERSION" || die "version $VERSION isn't released (try: install.sh list)"
}

# ── commands ────────────────────────────────────────────────────────────────
cmd_install() {
  check_platform
  if [ -z "${VERSION:-}" ]; then
    VERSION="$(latest_version)"
    [ -n "$VERSION" ] || die "couldn't find the latest release of $REPO (no releases yet, or GitHub is unreachable)"
  fi
  valid_version "$VERSION" || die "'$VERSION' is not a version number"
  choose_scope
  install_version "$VERSION" "$SCOPE"
}

cmd_update() {
  check_platform
  local latest; latest="$(latest_version)"
  [ -n "$latest" ] || die "couldn't find the latest release of $REPO"
  local any=0
  for scope in user system; do
    local dir v; dir="$(dir_for_scope "$scope")"; v="$(installed_version "$dir/hoard")"
    [ -n "$v" ] || continue
    any=1
    if version_lt "$v" "$latest"; then
      info "update available for $dir: $v → $latest"
      if confirm "  Update?" Y; then install_version "$latest" "$scope"; fi
    else
      ok "Hoard $v in $dir is up to date (latest is $latest)"
    fi
  done
  if [ "$any" = 0 ]; then
    warn "Hoard isn't installed yet"
    if confirm "Install Hoard $latest now?" Y; then VERSION="$latest"; cmd_install; fi
  fi
}

cmd_uninstall() {
  local removed=0
  for scope in user system; do
    if [ -n "${SCOPE:-}" ] && [ "$SCOPE" != "$scope" ]; then continue; fi
    local dir v; dir="$(dir_for_scope "$scope")"; v="$(installed_version "$dir/hoard")"
    [ -e "$dir/hoard" ] || continue
    if confirm "Remove Hoard ${v:-?} from $dir?" Y; then
      run_in "$dir" rm -f "$dir/hoard"
      ok "removed $dir/hoard"
      removed=1
    fi
  done
  [ "$removed" = 1 ] || say "  Nothing was removed."
  local d data=0
  for d in "${CONFIG_DIRS[@]}"; do [ -e "$d" ] && data=1; done
  if [ "$data" = 1 ]; then
    local want=0
    if [ "${PURGE:-0}" = 1 ]; then want=1
    elif [ "$ASSUME_YES" != 1 ] && confirm "Also delete Hoard's settings and data (${CONFIG_DIRS[*]})?" N; then want=1; fi
    if [ "$want" = 1 ]; then rm -rf "${CONFIG_DIRS[@]}"; ok "settings and data removed"; fi
  fi
  if grep -qs "# Added by the Hoard installer" "$HOME/.zshrc" "$HOME/.bash_profile" "$HOME/.profile" 2>/dev/null; then
    say "  ${C_MUT}The PATH line the installer added to your shell profile was left in place (other tools may use ~/.local/bin).${C_0}"
  fi
}

cmd_list() {
  local latest; latest="$(latest_version)"
  local versions; versions="$(available_versions)"
  [ -n "$versions" ] || die "no releases found for $REPO (or GitHub is unreachable)"
  echo "$versions" | while IFS= read -r v; do
    [ -n "$v" ] || continue
    if [ "$v" = "$latest" ]; then echo "$v (latest)"; else echo "$v"; fi
  done
}

usage() {
  sed -n '2,24p' "$0" 2>/dev/null | sed 's/^# \{0,1\}//' || true
  say "Commands: install, update, uninstall, list, status, help"
}

menu() {
  banner
  status
  say ""
  say "  What would you like to do?"
  say "    1) install or update to the latest version"
  say "    2) install a specific version"
  say "    3) uninstall"
  say "    4) list available versions"
  say "    5) quit"
  ask "  Choose 1–5:" 1
  case "$REPLY" in
    1) if [ -n "$(installed_version "$USER_DIR/hoard")$(installed_version "$SYSTEM_DIR/hoard")" ]; then cmd_update; else cmd_install; fi ;;
    2) check_platform; choose_version; choose_scope; install_version "$VERSION" "$SCOPE" ;;
    3) cmd_uninstall ;;
    4) cmd_list ;;
    *) say "  Bye." ;;
  esac
}

# ── arguments ───────────────────────────────────────────────────────────────
COMMAND=""
VERSION=""
SCOPE=""
PURGE=0
while [ $# -gt 0 ]; do
  case "$1" in
    install|update|upgrade|uninstall|remove|list|status|help) COMMAND="$1" ;;
    --version|-v) [ $# -ge 2 ] || die "--version needs a value"; VERSION="${2#v}"; shift ;;
    --version=*) VERSION="${1#--version=}"; VERSION="${VERSION#v}" ;;
    --user) SCOPE=user ;;
    --system|--all-users) SCOPE=system ;;
    --purge) PURGE=1 ;;
    --yes|-y) ASSUME_YES=1 ;;
    --help|-h) COMMAND=help ;;
    *) die "unknown argument '$1' (try --help)" ;;
  esac
  shift
done

case "$COMMAND" in
  install) banner; cmd_install ;;
  update|upgrade) banner; cmd_update ;;
  uninstall|remove) banner; cmd_uninstall ;;
  list) cmd_list ;;
  status) status ;;
  help) usage ;;
  "")
    if [ -n "$TTY" ] && [ "$ASSUME_YES" != 1 ]; then menu
    else banner; cmd_install; fi ;;
esac
