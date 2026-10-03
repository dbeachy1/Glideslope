#!/bin/sh
set -eu

usage() {
    echo "Usage: $0 --publish-dir DIR --version APP_VERSION --output-deb FILE" >&2
    exit 2
}

publish_dir=
output_deb=
version=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --publish-dir)
            [ "$#" -ge 2 ] || usage
            publish_dir=$2
            shift 2
            ;;
        --output-deb)
            [ "$#" -ge 2 ] || usage
            output_deb=$2
            shift 2
            ;;
        --version)
            [ "$#" -ge 2 ] || usage
            version=$2
            shift 2
            ;;
        *) usage ;;
    esac
done
[ -n "$publish_dir" ] && [ -n "$output_deb" ] && [ -n "$version" ] || usage

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
publish_dir=$(CDPATH= cd -- "$publish_dir" && pwd)
output_parent=$(dirname -- "$output_deb")
mkdir -p -- "$output_parent"
output_parent=$(CDPATH= cd -- "$output_parent" && pwd)
output_deb="$output_parent/$(basename -- "$output_deb")"

if [ -e "$output_deb" ]; then
    echo "Refusing to overwrite existing package: $output_deb" >&2
    exit 1
fi

for command_name in dpkg-deb dpkg-shlibdeps readelf sha256sum; do
    command -v "$command_name" >/dev/null 2>&1 || {
        echo "Required packaging tool is unavailable: $command_name" >&2
        exit 1
    }
done

case "$version" in
    ''|*[!0-9A-Za-z.+~-]*) echo "Invalid app package version: $version" >&2; exit 1 ;;
esac

app_binary="$publish_dir/Glideslope.App"
launcher="$publish_dir/glideslope-launcher.sh"
icon="$publish_dir/Assets/glideslope-icon.png"
for required in "$app_binary" "$launcher" "$icon" "$script_dir/Glideslope.desktop"; do
    [ -f "$required" ] || { echo "Required package input is missing: $required" >&2; exit 1; }
done
license_files='LICENSE NOTICE TRADEMARKS.md THIRD-PARTY-NOTICES.txt'
for name in $license_files; do
    required="$publish_dir/$name"
    [ -f "$required" ] && [ -s "$required" ] || {
        echo "Required licensing file is missing or empty: $required" >&2
        exit 1
    }
done
for section in 'Avalonia 12.1.3' 'ANGLE Windows native package' \
    'HarfBuzzSharp, HarfBuzzSharp.NativeAssets' 'SkiaSharp, SkiaSharp.NativeAssets' \
    'MicroCom.Runtime 0.11.6' 'Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core 10.0.12' \
    'SQLitePCLRaw.bundle_e_sqlite3' 'SQLite e_sqlite3 native library' \
    'Tmds.DBus.Protocol 0.94.1' '.NET self-contained runtime 10.0.12 for win-x64 and linux-x64' \
    'License notice for ASP.NET'; do
    grep -F "$section" "$publish_dir/THIRD-PARTY-NOTICES.txt" >/dev/null || {
        echo "Third-party notices are missing required coverage: $section" >&2
        exit 1
    }
done
[ -f "$app_binary" ] || { echo "Published app is not a regular file: $app_binary" >&2; exit 1; }
grep -F 'DOTNET_BUNDLE_EXTRACT_BASE_DIR=' "$launcher" >/dev/null || {
    echo "Linux launcher does not configure the single-file extraction cache." >&2
    exit 1
}
grep -F 'exec "$script_dir/Glideslope.App" "$@"' "$launcher" >/dev/null || {
    echo "Linux launcher does not resolve beside itself and forward arguments." >&2
    exit 1
}

machine=$(readelf -h "$app_binary" | sed -n 's/^[[:space:]]*Machine:[[:space:]]*//p')
case "$machine" in
    *X86-64*) ;;
    *) echo "Expected an x86-64 publish, got: $machine" >&2; exit 1 ;;
esac

temp_base=${TMPDIR:-/tmp}
temp_base=$(CDPATH= cd -- "$temp_base" && pwd)
stage_root=$(mktemp -d "$temp_base/Glideslope.Packaging-deb.XXXXXX")
owner_marker=$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')
printf '%s\n' "$owner_marker" > "$stage_root/.glideslope-package-owner"
cleanup() {
    if [ -d "$stage_root" ]; then
        canonical=$(CDPATH= cd -- "$stage_root" && pwd)
        [ "$(dirname -- "$canonical")" = "$temp_base" ] || {
            echo "Refusing to remove an unexpected packaging temp path: $canonical" >&2
            return 1
        }
        case "$(basename -- "$canonical")" in Glideslope.Packaging-deb.*) ;; *)
            echo "Refusing to remove a non-owned packaging temp root: $canonical" >&2
            return 1
            ;; esac
        [ "$(cat "$canonical/.glideslope-package-owner")" = "$owner_marker" ] || {
            echo "Refusing to remove a temp root with a changed ownership marker." >&2
            return 1
        }
        rm -rf -- "$canonical"
        [ ! -e "$canonical" ] || { echo "Packaging temp cleanup failed: $canonical" >&2; return 1; }
    fi
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

install_root="$stage_root/root"
mkdir -p "$install_root/usr/lib/glideslope" \
    "$install_root/usr/share/applications" \
    "$install_root/usr/share/icons/hicolor/256x256/apps" \
    "$install_root/DEBIAN"
cp -a "$publish_dir/." "$install_root/usr/lib/glideslope/"
install -m 0644 "$script_dir/Glideslope.desktop" "$install_root/usr/share/applications/glideslope.desktop"
install -m 0644 "$icon" "$install_root/usr/share/icons/hicolor/256x256/apps/glideslope.png"
find "$install_root/usr/lib/glideslope" -type d -exec chmod 0755 {} +
find "$install_root/usr/lib/glideslope" -type f -exec chmod 0644 {} +
chmod 0755 "$install_root/usr/lib/glideslope/Glideslope.App"
chmod 0755 "$install_root/usr/lib/glideslope/glideslope-launcher.sh"
staged_app="$install_root/usr/lib/glideslope/Glideslope.App"

# The Linux single-file bundle extracts native shared libraries before managed Main.
# Exercise only the headless registration command so dependency analysis sees the
# exact frozen bundle's .so files without opening Avalonia or touching user paths.
bundle_cache="$stage_root/bundle-cache"
proof_root="$stage_root/bundle-proof"
mkdir -p "$bundle_cache" "$proof_root" "$stage_root/home" "$stage_root/xdg-cache"
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$bundle_cache"
export HOME="$stage_root/home"
export XDG_CACHE_HOME="$stage_root/xdg-cache"
timeout --signal=TERM --kill-after=5s 45s \
    "$staged_app" --startup-registration disable --proof-root "$proof_root" >/dev/null 2>&1 || {
    echo "Frozen Linux bundle's headless startup command failed before dependency inspection." >&2
    exit 1
}
native_libraries="$stage_root/native-libraries.txt"
find "$bundle_cache" -type f -name '*.so*' -print > "$native_libraries"
if [ ! -s "$native_libraries" ]; then
    echo "The frozen Linux bundle did not extract its expected native libraries." >&2
    exit 1
fi

analysis_root="$stage_root/dependency-analysis"
mkdir -p "$analysis_root/debian"
cat > "$analysis_root/debian/control" <<EOF
Source: glideslope
Section: utils
Priority: optional
Maintainer: Glideslope maintainers
Standards-Version: 4.6.2

Package: glideslope
Architecture: amd64
Description: Subscription coding usage monitor
 A private desktop monitor for read-only coding quota usage.
EOF
# Include every native library extracted from the actual frozen single-file bundle,
# as well as the apphost; this catches GUI libraries absent from apphost NEEDED entries.
set -- "$staged_app"
while IFS= read -r native_library; do
    set -- "$@" "$native_library"
done < "$native_libraries"
dependency_output=$(cd "$analysis_root" && dpkg-shlibdeps -O "$@") || {
    echo "Could not derive runtime dependencies from the published executable." >&2
    exit 1
}
depends=${dependency_output#shlibs:Depends=}
[ "$depends" != "$dependency_output" ] || {
    echo "dpkg-shlibdeps returned no Depends field: $dependency_output" >&2
    exit 1
}
# Avalonia's Linux platform documentation lists these runtime packages for the
# X11 desktop backend; some are loaded dynamically and are invisible to NEEDED scans.
# See https://docs.avaloniaui.net/docs/supported-platforms#desktop-linux.
for package in libx11-6 libice6 libsm6 libfontconfig1; do
    if ! printf '%s\n' "$depends" | grep -Eq "(^|[ ,])${package}([ (,]|$)"; then
        depends="$depends, $package"
    fi
done

cat > "$install_root/DEBIAN/control" <<EOF
Package: glideslope
Version: $version
Section: utils
Priority: optional
Architecture: amd64
Maintainer: Glideslope maintainers
Depends: $depends
Description: Subscription coding usage monitor
 A private desktop monitor for read-only coding quota usage.
EOF

# Normalize every package path after staging so the host's umask and copied NTFS
# metadata cannot leave package directories or data files group/world writable.
find "$install_root" -type d -exec chmod 0755 {} +
find "$install_root" -type f -exec chmod 0644 {} +
chmod 0755 "$install_root/usr/lib/glideslope/Glideslope.App"
chmod 0755 "$install_root/usr/lib/glideslope/glideslope-launcher.sh"

dpkg-deb --build --root-owner-group "$install_root" "$output_deb"
dpkg-deb --info "$output_deb"
test "$(dpkg-deb --field "$output_deb" Package)" = glideslope
test "$(dpkg-deb --field "$output_deb" Version)" = "$version"
test "$(dpkg-deb --field "$output_deb" Architecture)" = amd64
dpkg-deb --contents "$output_deb" > "$output_deb.contents.txt"
dpkg-deb --field "$output_deb" Depends > "$output_deb.depends.txt"
(cd "$output_parent" && sha256sum "$(basename -- "$output_deb")") > "$output_deb.sha256"
while IFS= read -r native_library; do
    printf '%s\n' "${native_library#"$bundle_cache"/}"
done < "$native_libraries" > "$output_deb.native-libraries.txt"
package_root="$stage_root/extracted"
mkdir -p "$package_root"
dpkg-deb --extract "$output_deb" "$package_root"
for name in $license_files; do
    staged="$package_root/usr/lib/glideslope/$name"
    [ -f "$staged" ] && [ -s "$staged" ] || {
        echo "Extracted Debian package is missing a readable licensing file: $staged" >&2
        exit 1
    }
    cmp -s "$publish_dir/$name" "$staged" || {
        echo "Extracted Debian licensing file changed: $name" >&2
        exit 1
    }
done
"$script_dir/verify-startup-command.sh" --package-root "$package_root"
printf 'Built and inspected %s (version %s, amd64).\n' "$output_deb" "$version"
