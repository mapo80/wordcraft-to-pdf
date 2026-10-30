#!/bin/bash
# pack-nuget.sh — Build the WordCraftPdf NuGet packages from scripts/build.sh output
#
# Usage:
#   ./scripts/pack-nuget.sh                                   # current platform, worker from output/
#   ./scripts/pack-nuget.sh macos output-macos-arm64/ output-macos-x64/
#   ./scripts/pack-nuget.sh linux output-linux-x64/ output-linux-arm64/
#   ./scripts/pack-nuget.sh linux output-linux-x64/           # single-arch OK
#   ./scripts/pack-nuget.sh windows output-win-x64/ output-win-arm64/
#
# Each native dir is a scripts/build.sh output folder: bin/wcpdf_worker[.exe] + licenses/.
# The first dir is x64 for linux/windows and arm64 for macos (as in SlimLO); the optional
# second dir is the other architecture. Without dirs, output/ is packed for the current
# platform and architecture.
#
# Environment:
#   VERSION   package version override (e.g. VERSION=0.2.0)
#
# Produces:
#   dotnet/nupkgs/WordCraftPdf.<version>.nupkg
#   dotnet/nupkgs/WordCraftPdf.NativeAssets.{Linux,macOS,Windows}.<version>.nupkg
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
NUPKG_DIR="$ROOT_DIR/dotnet/nupkgs"

detect_platform() {
    case "$(uname -s)" in
        Darwin) echo "macos" ;;
        Linux) echo "linux" ;;
        CYGWIN*|MINGW*|MSYS*) echo "windows" ;;
        *) echo "Error: unsupported host $(uname -s); pass the platform explicitly" >&2; exit 1 ;;
    esac
}

detect_arch() {
    case "$(uname -m)" in
        x86_64|amd64) echo "x64" ;;
        arm64|aarch64) echo "arm64" ;;
        *) echo "Error: unsupported architecture $(uname -m)" >&2; exit 1 ;;
    esac
}

abs_dir() {
    if [ ! -d "$1" ]; then
        echo "Error: native dir not found: $1" >&2
        exit 1
    fi
    (cd "$1" && pwd)
}

PLATFORM="${1:-$(detect_platform)}"
NATIVE_DIR_1="${2:-}"
NATIVE_DIR_2="${3:-}"

case "$PLATFORM" in
    linux)   DISPLAY_NAME="Linux";   FIRST_ARCH="x64";   SECOND_ARCH="arm64"; WORKER="wcpdf_worker" ;;
    macos)   DISPLAY_NAME="macOS";   FIRST_ARCH="arm64"; SECOND_ARCH="x64";   WORKER="wcpdf_worker" ;;
    windows) DISPLAY_NAME="Windows"; FIRST_ARCH="x64";   SECOND_ARCH="arm64"; WORKER="wcpdf_worker.exe" ;;
    *)
        echo "Error: Unknown platform '$PLATFORM'. Supported: linux, macos, windows"
        exit 1
        ;;
esac

# Map the dirs to architectures
DIR_X64=""
DIR_ARM64=""
if [ -z "$NATIVE_DIR_1" ]; then
    # Default: output/ for the current architecture
    if [ "$(detect_arch)" = "x64" ]; then DIR_X64="$(abs_dir "$ROOT_DIR/output")"; else DIR_ARM64="$(abs_dir "$ROOT_DIR/output")"; fi
else
    FIRST="$(abs_dir "$NATIVE_DIR_1")"
    if [ "$FIRST_ARCH" = "x64" ]; then DIR_X64="$FIRST"; else DIR_ARM64="$FIRST"; fi
    if [ -n "$NATIVE_DIR_2" ]; then
        SECOND="$(abs_dir "$NATIVE_DIR_2")"
        if [ "$SECOND_ARCH" = "x64" ]; then DIR_X64="$SECOND"; else DIR_ARM64="$SECOND"; fi
    fi
fi

for d in "$DIR_X64" "$DIR_ARM64"; do
    if [ -n "$d" ] && [ ! -f "$d/bin/$WORKER" ]; then
        echo "Error: $d/bin/$WORKER not found (run scripts/build.sh first)" >&2
        exit 1
    fi
done

mkdir -p "$NUPKG_DIR"

# Version override via VERSION env var (used by release workflow)
VERSION_ARGS=()
if [ -n "${VERSION:-}" ]; then
    VERSION_ARGS=(-p:Version="$VERSION")
    echo "Using version override: $VERSION"
fi

# ── Managed package ──────────────────────────────────────────────────
echo "=== Packing WordCraftPdf managed package ==="
dotnet pack "$ROOT_DIR/dotnet/WordCraftPdf/WordCraftPdf.csproj" \
    -c Release \
    -o "$NUPKG_DIR" \
    ${VERSION_ARGS[@]+"${VERSION_ARGS[@]}"}

# ── Native assets package ────────────────────────────────────────────
CSPROJ="$ROOT_DIR/dotnet/WordCraftPdf.NativeAssets.$DISPLAY_NAME/WordCraftPdf.NativeAssets.$DISPLAY_NAME.csproj"
# Unused architectures point at a folder that doesn't exist, so the defaults never leak in.
NONE="$ROOT_DIR/.no-native-dir/"
PACK_ARGS=(
    -p:NativeDir_x64="${DIR_X64:-$NONE}/"
    -p:NativeDir_arm64="${DIR_ARM64:-$NONE}/"
)

echo ""
echo "=== Packing WordCraftPdf.NativeAssets.$DISPLAY_NAME (x64: ${DIR_X64:-none}, arm64: ${DIR_ARM64:-none}) ==="
dotnet pack "$CSPROJ" \
    -c Release \
    -o "$NUPKG_DIR" \
    "${PACK_ARGS[@]}" \
    ${VERSION_ARGS[@]+"${VERSION_ARGS[@]}"}

echo ""
echo "=== Packages created ==="
ls -la "$NUPKG_DIR"/*.nupkg 2>/dev/null || echo "No .nupkg files found"
