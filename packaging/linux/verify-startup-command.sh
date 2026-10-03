#!/bin/sh
set -eu

usage() {
    echo "Usage: $0 --package-root EXTRACTED_PACKAGE_ROOT" >&2
    exit 2
}

package_root=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --package-root)
            [ "$#" -ge 2 ] || usage
            package_root=$2
            shift 2
            ;;
        *) usage ;;
    esac
done
[ -n "$package_root" ] || usage
package_root=$(CDPATH= cd -- "$package_root" && pwd)
app="$package_root/usr/lib/glideslope/Glideslope.App"
launcher="$package_root/usr/lib/glideslope/glideslope-launcher.sh"
[ -x "$app" ] && [ -x "$launcher" ] || { echo "Extracted package is missing its app or launcher." >&2; exit 1; }

temp_base=${TMPDIR:-/tmp}
temp_base=$(CDPATH= cd -- "$temp_base" && pwd)
proof_root=$(mktemp -d "$temp_base/Glideslope.Packaging-LinuxStartup.XXXXXX")
owner_marker=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
printf '%s\n' "$owner_marker" > "$proof_root/.glideslope-owner"
cleanup() {
    if [ -d "$proof_root" ]; then
        canonical=$(CDPATH= cd -- "$proof_root" && pwd)
        [ "$(dirname -- "$canonical")" = "$temp_base" ] || { echo "Unexpected proof root: $canonical" >&2; return 1; }
        case "$(basename -- "$canonical")" in Glideslope.Packaging-LinuxStartup.*) ;; *) echo "Unexpected proof root name: $canonical" >&2; return 1 ;; esac
        [ "$(cat "$canonical/.glideslope-owner")" = "$owner_marker" ] || { echo "Proof root ownership marker changed." >&2; return 1; }
        rm -rf -- "$canonical"
        [ ! -e "$canonical" ] || { echo "Proof root cleanup failed: $canonical" >&2; return 1; }
    fi
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

home="$proof_root/home"
cache="$proof_root/cache"
mkdir -p "$home" "$cache"
export HOME="$home"
export XDG_CONFIG_HOME="$proof_root/xdg-config"
export XDG_CACHE_HOME="$cache"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$cache/Glideslope/bundle"

run_app() {
    timeout --signal=TERM --kill-after=5s 45s "$app" "$@" >/dev/null 2>&1
}

run_app --startup-registration enable --proof-root "$proof_root"
startup="$proof_root/startup/Glideslope.desktop"
[ -f "$startup" ] || { echo "The extracted app did not create the proof-root startup artifact." >&2; exit 1; }
grep -F "Exec=\"$launcher\" --autostart" "$startup" >/dev/null
grep -Fx "TryExec=$launcher" "$startup" >/dev/null
grep -Fx 'X-Glideslope-Managed=true' "$startup" >/dev/null
"$launcher" --startup-registration disable --proof-root "$proof_root" >/dev/null 2>&1 || {
    echo "The extracted package launcher failed." >&2
    exit 1
}
[ -d "$DOTNET_BUNDLE_EXTRACT_BASE_DIR" ] || { echo "Launcher did not create its isolated extraction cache." >&2; exit 1; }
run_app --startup-registration disable --proof-root "$proof_root"
[ ! -e "$startup" ] || { echo "Disable did not remove its owned startup artifact." >&2; exit 1; }

mkdir -p "$(dirname -- "$startup")"
printf '%s\n' 'foreign user content' > "$startup"
if run_app --startup-registration disable --proof-root "$proof_root"; then
    echo "Disable unexpectedly removed or accepted a foreign startup artifact." >&2
    exit 1
else
    result=$?
    [ "$result" -eq 1 ] || { echo "Foreign-artifact disable returned unexpected status $result." >&2; exit 1; }
fi
[ "$(cat "$startup")" = 'foreign user content' ] || { echo "Foreign startup content changed." >&2; exit 1; }
rm -- "$startup"

missing_launcher="$proof_root/missing-launcher"
mkdir -p "$missing_launcher"
cp -- "$app" "$missing_launcher/Glideslope.App"
chmod 0755 "$missing_launcher/Glideslope.App"
if timeout --signal=TERM --kill-after=5s 45s "$missing_launcher/Glideslope.App" --startup-registration enable --proof-root "$proof_root/missing-proof" >/dev/null 2>&1; then
    echo "Startup command succeeded without its adjacent launcher." >&2
    exit 1
else
    result=$?
    [ "$result" -eq 2 ] || { echo "Missing-launcher command returned unexpected status $result." >&2; exit 1; }
fi
[ ! -e "$proof_root/missing-proof/startup/Glideslope.desktop" ] || { echo "Missing-launcher command wrote a startup artifact." >&2; exit 1; }

printf 'Extracted Linux startup command proof passed: isolated write/remove, foreign artifact preservation, missing launcher failure, and adjacent launcher cache.\n'
