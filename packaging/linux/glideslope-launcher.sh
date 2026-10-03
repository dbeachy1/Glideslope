#!/bin/sh
set -eu
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cache_root=${XDG_CACHE_HOME:-"$HOME/.cache"}
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$cache_root/Glideslope/bundle"
mkdir -p "$DOTNET_BUNDLE_EXTRACT_BASE_DIR"
exec "$script_dir/Glideslope.App" "$@"
