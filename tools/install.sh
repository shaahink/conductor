#!/usr/bin/env bash
#
# Build the Conductor engine + the Go face and install a global `conductor` command.
# POSIX twin of tools/install.ps1 — same three steps, same result:
#
#   1. publish the C# engine (Release by default) to an install dir,
#   2. build the Go face (conductor-face) RIGHT NEXT TO the engine, where FaceLauncher looks for it
#      first, so `conductor run` auto-spawns the TUI with no extra flags,
#   3. symlink `conductor` into a bin dir that is (usually) already on your PATH.
#
# Re-run after code changes to update the installed command. This is "cut a local release": the
# installed `conductor` is a snapshot, independent of the repo's Debug build.
#
# If you only want to SEE it work, you do not need this script at all — grab a release binary and
# run `conductor demo`.
set -euo pipefail

INSTALL_DIR="${CONDUCTOR_INSTALL_DIR:-${XDG_DATA_HOME:-$HOME/.local/share}/conductor}"
BIN_DIR="${CONDUCTOR_BIN_DIR:-$HOME/.local/bin}"
CONFIG="Release"

usage() {
    cat <<'EOF'
usage: tools/install.sh [--install-dir DIR] [--bin-dir DIR] [--config Release|Debug]

  --install-dir DIR   where the engine + face land   (default: $XDG_DATA_HOME/conductor)
  --bin-dir DIR       where the `conductor` shim goes (default: ~/.local/bin)
  --config CFG        Release (default) or Debug
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --install-dir) INSTALL_DIR="$2"; shift 2 ;;
        --bin-dir)     BIN_DIR="$2";     shift 2 ;;
        --config)      CONFIG="$2";      shift 2 ;;
        -h|--help)     usage; exit 0 ;;
        *) echo "unknown option: $1" >&2; usage; exit 2 ;;
    esac
done

repo="$(cd "$(dirname "$0")/.." && pwd)"   # tools/ -> repo root

need() {
    command -v "$1" >/dev/null 2>&1 || {
        echo "error: '$1' is not on PATH — $2" >&2
        exit 1
    }
}
need dotnet "the engine targets net10.0 (https://dotnet.microsoft.com/)"
need go     "the Face is a Go binary (https://go.dev/)"
need git    "conductor verifies work by diffing commits; a run without git has no evidence"

# SC8.1: say which version we replaced and which we installed. Publishing in silence is why
# "rebuild before trusting it" had to be taken on faith. Falls back honestly on a binary that
# predates the `version` verb rather than inventing a number.
conductor_version() {
    if [ ! -x "$1" ]; then echo "(none installed)"; return 0; fi
    local out
    if out="$("$1" version --short 2>/dev/null)" && [ -n "$out" ]; then
        printf '%s\n' "$out" | head -n 1
        return 0
    fi
    echo "(unknown - predates the version verb)"
}

exe="$INSTALL_DIR/conductor"
before="$(conductor_version "$exe")"

echo "conductor installer"
echo "  repo:    $repo"
echo "  install: $INSTALL_DIR"
echo "  bin:     $BIN_DIR"
echo "  config:  $CONFIG"
echo "  current: $before"
echo

echo "[1/3] publishing engine..."
dotnet publish "$repo/src/Conductor/Conductor.csproj" -c "$CONFIG" -o "$INSTALL_DIR" --nologo -v q
[ -f "$exe" ] || { echo "error: expected $exe after publish, not found" >&2; exit 1; }
chmod +x "$exe"
after="$(conductor_version "$exe")"

echo "[2/3] building Go face..."
( cd "$repo/face-go" && go build -o "$INSTALL_DIR/conductor-face" ./cmd/conductor-face/ )

echo "[3/3] installing 'conductor' on PATH..."
mkdir -p "$BIN_DIR"
ln -sf "$exe" "$BIN_DIR/conductor"
echo "  shim: $BIN_DIR/conductor -> $exe"

echo
echo "Done."
echo "  version: $before  ->  $after"
# An `[ ... ] && echo` one-liner would trip `set -e` on the false branch. An if does not.
if [ "$before" = "$after" ]; then
    echo "  (unchanged - same commit, clean tree, and nothing to rebuild)"
fi
case ":${PATH}:" in
    *":${BIN_DIR}:"*)
        echo "Try it now:  conductor demo"
        ;;
    *)
        echo "NOTE: $BIN_DIR is not on your PATH. Add this to your shell profile:"
        echo "  export PATH=\"\$PATH:$BIN_DIR\""
        echo "Then:  conductor demo"
        ;;
esac
