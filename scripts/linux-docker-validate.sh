#!/bin/bash
# linux-docker-validate.sh — Build WordCraftPdf for Linux in Docker, extract the artifacts and
# run the release gate in a runtime-like container (Debian slim + only what the gate needs).
#
#   [1/4] build + extract the artifacts       -> $OUTPUT_DIR (default output-linux-docker-<arch>/)
#   [2/4] gate image (runtime base + cc + python3), run scripts/run-gate.sh /opt/wcpdf
#   [3/4] runtime image: worker answers init over stdin, no missing shared libraries
#   [4/4] summary (image size, throughput)
#
# Environment:
#   PLATFORM        linux/amd64 | linux/arm64 (default: the Docker host's)
#   OUTPUT_SUBDIR   project-relative output folder (default output-linux-docker-<arch>)
#   NPROC           compile jobs inside the build (default: the builder's CPUs)
#   GATE_ITERATIONS, GATE_MIN_THROUGHPUT   passed to the gate
#   SKIP_BUILD=1    reuse the images from a previous run
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
DOCKERFILE="$PROJECT_DIR/docker/Dockerfile.linux"
SKIP_BUILD="${SKIP_BUILD:-0}"

command -v docker >/dev/null 2>&1 || { echo "ERROR: docker is required"; exit 1; }
docker info >/dev/null 2>&1 || { echo "ERROR: the Docker daemon isn't running"; exit 1; }

PLATFORM="${PLATFORM:-}"
if [ -z "$PLATFORM" ]; then
    case "$(docker info --format '{{.Architecture}}')" in
        aarch64|arm64) PLATFORM="linux/arm64" ;;
        *)             PLATFORM="linux/amd64" ;;
    esac
fi
ARCH="${PLATFORM#linux/}"
OUTPUT_SUBDIR="${OUTPUT_SUBDIR:-output-linux-docker-$ARCH}"
case "$OUTPUT_SUBDIR" in
    /*|*..*) echo "ERROR: OUTPUT_SUBDIR must be project-relative without '..' (got $OUTPUT_SUBDIR)"; exit 1 ;;
esac
OUTPUT_DIR="$PROJECT_DIR/$OUTPUT_SUBDIR"
GATE_IMAGE="wordcraftpdf-gate:$ARCH"
RUNTIME_IMAGE="wordcraftpdf:validate-$ARCH"
COMMIT="$(git -C "$PROJECT_DIR" rev-parse --verify -q HEAD 2>/dev/null || true)"
BUILD_ARGS=(--platform "$PLATFORM" -f "$DOCKERFILE" --build-arg "NPROC=${NPROC:-}" --build-arg "WCPDF_COMMIT=$COMMIT")

echo "=== WordCraftPdf Linux Docker validation ==="
echo "Platform: $PLATFORM"
echo "Output:   $OUTPUT_DIR"
echo ""

if [ "$SKIP_BUILD" != "1" ]; then
    echo ">>> [1/4] Building and extracting artifacts"
    rm -rf "$OUTPUT_DIR"
    DOCKER_BUILDKIT=1 docker build "${BUILD_ARGS[@]}" --target artifacts \
        --output "type=local,dest=$OUTPUT_DIR" "$PROJECT_DIR"
    chmod +x "$OUTPUT_DIR/bin/"* 2>/dev/null || true
    echo ""
    echo ">>> [1/4] Building gate and runtime images"
    DOCKER_BUILDKIT=1 docker build "${BUILD_ARGS[@]}" --target gate -t "$GATE_IMAGE" "$PROJECT_DIR"
    DOCKER_BUILDKIT=1 docker build "${BUILD_ARGS[@]}" --target runtime -t "$RUNTIME_IMAGE" "$PROJECT_DIR"
else
    echo ">>> [1/4] Skipping build (SKIP_BUILD=1)"
    echo "    Reusing $GATE_IMAGE and $RUNTIME_IMAGE"
fi
echo ""

echo ">>> [2/4] Running the gate in a container"
mkdir -p "$OUTPUT_DIR/gate"
docker run --rm --platform "$PLATFORM" \
    -e GATE_ITERATIONS="${GATE_ITERATIONS:-5}" \
    -e GATE_MIN_THROUGHPUT="${GATE_MIN_THROUGHPUT:-0}" \
    -e GATE_STRICT_METADATA="${GATE_STRICT_METADATA:-1}" \
    -v "$OUTPUT_DIR/gate:/reports" \
    "$GATE_IMAGE" | tee "$OUTPUT_DIR/gate/gate-linux.log"
echo ""

echo ">>> [3/4] Runtime image checks"
# No missing shared libraries in the slim image.
if docker run --rm --platform "$PLATFORM" --entrypoint ldd "$RUNTIME_IMAGE" /opt/wcpdf/bin/wcpdf_worker | tee /dev/stderr | grep -q 'not found'; then
    echo "FAIL: missing shared libraries in the runtime image"
    exit 1
fi
# The worker answers init over stdin/stdout (docs/protocol.md), as the non-root user.
docker run -i --rm --platform "$PLATFORM" "$RUNTIME_IMAGE" < <(
    python3 -I -c '
import json, struct, sys
for m in ({"type": "init"}, {"type": "quit"}):
    b = json.dumps(m).encode()
    sys.stdout.buffer.write(struct.pack("<I", len(b)) + b)
') | python3 -I -c '
import json, struct, sys
data = sys.stdin.buffer.read()
if len(data) < 4:
    sys.exit("FAIL: no response from the worker")
(n,) = struct.unpack("<I", data[:4])
r = json.loads(data[4:4 + n])
print("  worker:", r.get("type"), r.get("version"))
sys.exit(0 if r.get("type") == "ready" else 1)
'
echo "PASS: runtime image"
echo ""

echo ">>> [4/4] Summary"
docker images --format '  {{.Repository}}:{{.Tag}}  {{.Size}}' | grep -E "^  (wordcraftpdf|wordcraftpdf-gate):" || true
[ -f "$OUTPUT_DIR/gate/throughput.json" ] && sed 's/^/  /' "$OUTPUT_DIR/gate/throughput.json"
echo ""
echo "PASS: Linux Docker validation ($PLATFORM)"
