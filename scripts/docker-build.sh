#!/bin/bash
# docker-build.sh — Build WordCraftPdf for Linux in Docker and extract the artifacts.
#
# Usage:
#   ./scripts/docker-build.sh                          # host arch -> output-linux-<arch>/
#   ./scripts/docker-build.sh --platform linux/arm64   # another arch (buildx + QEMU or a native node)
#   ./scripts/docker-build.sh --runtime                # also tag the runtime image
#   ./scripts/docker-build.sh --output out/            # extract somewhere else
#
# Environment:
#   NPROC             parallel compile jobs inside the build (default: the builder's CPUs)
#   WITH_CRAFT_FONTS  1 (default) / 0, passed to scripts/build.sh
#   RUNTIME_IMAGE     runtime image tag (default: wordcraftpdf:<version>-<arch> and wordcraftpdf:latest)
#
# Needs Docker with BuildKit (Docker 23+, or docker buildx).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DOCKERFILE="$PROJECT_DIR/docker/Dockerfile.linux"

PLATFORM=""
OUTPUT_DIR=""
BUILD_RUNTIME=0
while [ $# -gt 0 ]; do
    case "$1" in
        --platform) PLATFORM="${2:?--platform needs a value, e.g. linux/arm64}"; shift 2 ;;
        --output)   OUTPUT_DIR="${2:?--output needs a directory}"; shift 2 ;;
        --runtime)  BUILD_RUNTIME=1; shift ;;
        -h|--help)  sed -n '2,16p' "$0"; exit 0 ;;
        *) echo "Unknown option: $1 (see --help)"; exit 1 ;;
    esac
done

command -v docker >/dev/null 2>&1 || { echo "ERROR: docker not found"; exit 1; }
docker info >/dev/null 2>&1 || { echo "ERROR: the Docker daemon isn't running"; exit 1; }

if [ -z "$PLATFORM" ]; then
    case "$(docker info --format '{{.Architecture}}' 2>/dev/null || uname -m)" in
        aarch64|arm64) PLATFORM="linux/arm64" ;;
        *)             PLATFORM="linux/amd64" ;;
    esac
fi
case "$PLATFORM" in
    linux/amd64) ARCH="x64" ;;
    linux/arm64) ARCH="arm64" ;;
    *) echo "ERROR: unsupported platform $PLATFORM (linux/amd64 or linux/arm64)"; exit 1 ;;
esac
OUTPUT_DIR="${OUTPUT_DIR:-$PROJECT_DIR/output-linux-$ARCH}"
VERSION="$(sed -n 's/^version = "\(.*\)"/\1/p' "$PROJECT_DIR/Cargo.toml" | head -1)"
COMMIT="$(git -C "$PROJECT_DIR" rev-parse --verify -q HEAD 2>/dev/null || true)"

BUILD_ARGS=(
    --platform "$PLATFORM"
    -f "$DOCKERFILE"
    --build-arg "NPROC=${NPROC:-}"
    --build-arg "WITH_CRAFT_FONTS=${WITH_CRAFT_FONTS:-1}"
    --build-arg "WCPDF_COMMIT=$COMMIT"
)

echo "============================================"
echo " WordCraftPdf Docker build"
echo " Platform: $PLATFORM"
echo " Output:   $OUTPUT_DIR"
echo "============================================"
echo ""

echo ">>> Building and extracting artifacts..."
rm -rf "$OUTPUT_DIR"
DOCKER_BUILDKIT=1 docker build "${BUILD_ARGS[@]}" \
    --target artifacts \
    --output "type=local,dest=$OUTPUT_DIR" \
    "$PROJECT_DIR"
chmod +x "$OUTPUT_DIR/bin/"* 2>/dev/null || true
echo ""
echo ">>> Artifacts:"
find "$OUTPUT_DIR" -maxdepth 2 -type f | sort | sed "s|^$OUTPUT_DIR/|    |"
du -sh "$OUTPUT_DIR"

if [ "$BUILD_RUNTIME" -eq 1 ]; then
    TAG="${RUNTIME_IMAGE:-wordcraftpdf:$VERSION-$ARCH}"
    echo ""
    echo ">>> Building runtime image $TAG..."
    DOCKER_BUILDKIT=1 docker build "${BUILD_ARGS[@]}" --target runtime --load -t "$TAG" "$PROJECT_DIR" 2>/dev/null \
        || DOCKER_BUILDKIT=1 docker build "${BUILD_ARGS[@]}" --target runtime -t "$TAG" "$PROJECT_DIR"
    [ -z "${RUNTIME_IMAGE:-}" ] && docker tag "$TAG" wordcraftpdf:latest
    docker images "${TAG%%:*}" --format '    {{.Repository}}:{{.Tag}}  {{.Size}}'
fi

echo ""
echo "============================================"
echo " Done: $OUTPUT_DIR"
echo "============================================"
