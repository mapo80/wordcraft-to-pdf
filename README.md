# WordCraftPdf

**Fast DOCX-to-PDF conversion on the [WordCraft](https://github.com/storytold/wordcraft) engine — pure Rust, no LibreOffice — with .NET, Java and C APIs.**

[![Build](https://github.com/mapo80/wordcraft-to-pdf/actions/workflows/build.yml/badge.svg)](https://github.com/mapo80/wordcraft-to-pdf/actions/workflows/build.yml)
[![Release](https://github.com/mapo80/wordcraft-to-pdf/actions/workflows/release.yml/badge.svg)](https://github.com/mapo80/wordcraft-to-pdf/actions/workflows/release.yml)

WordCraftPdf turns Word documents (and DOC, ODT, RTF, HTML, Markdown, plain text) into PDF with
WordCraft's layout engine and PDF writer, linked as a library into a small native worker. There
is no office suite to install, no headless desktop, no scripting runtime: one self-contained
executable per platform, built from WordCraft's source at a pinned release tag.

| | |
|---|---|
| **Platforms** | Linux x64/arm64 · macOS arm64/x64 · Windows x64/ARM64 |
| **Formats in** | DOCX (DOCM, DOTX, DOTM) · DOC · ODT · RTF · HTML · Markdown · TXT |
| **Speed** | ~2–5 ms for a 1–2 page document, ~20–50 ms for 14–28 pages, ~100 conversions/s per worker (Apple M-series, warm) |
| **Worker** | one ~70 MB executable, fonts for CJK/Arabic/Thai/Hebrew… embedded; no runtime dependencies beyond the C library |
| **WordCraft** | `v0.12.0` (see [`WORDCRAFT_VERSION`](WORDCRAFT_VERSION)) |

> **Fidelity note.** WordCraft is young (alpha) and lays documents out with its own engine. Most
> documents convert well, but pagination can differ from Microsoft Word on complex layouts, and
> documents in Aptos (Word's default since 2024) wrap differently because no metric-compatible
> open font exists. Check your own documents; diagnostics report every font substitution.

---

## Architecture

```
                        ┌─────────────────────┐
                        │   Your Application  │
                        └──────────┬──────────┘
          ┌────────────────────────┼───────────────────────┐
          │                        │                       │
   .NET SDK (async)         Java SDK (sync+async)     C API (in process)
          │                        │                       │
 ┌────────┴────────┐    ┌──────────┴────────┐     #include "wcpdf.h"
 │  WorkerPool     │    │  WorkerPool       │              │
 │  idle-first     │    │  idle-first       │              │
 │  ├─ worker[0] ──┼─┐  │  ├─ worker[0] ────┼─┐            │
 │  └─ worker[N] ──┼─┤  │  └─ worker[N] ────┼─┤            │
 └─────────────────┘ │  └───────────────────┘ │            │
                     ▼    stdin/stdout pipes  ▼            ▼
              ┌─────────────────────────────────┐   ┌──────────────┐
              │  wcpdf_worker  (one process     │   │  libwcpdf    │
              │  per worker, framed JSON +      │   │  .so/.dylib/ │
              │  binary IPC)                    │   │  .dll        │
              └────────────────┬────────────────┘   └──────┬───────┘
                               └──────────────┬────────────┘
                                         wcpdf-core (safe Rust)
                                              │
                         WordCraft engine crates, built from source
                     docx/doc/odt readers → layout → PDF writer (Krilla)
```

**Key design decisions:**

- **WordCraft as a library, not a CLI.** The worker calls WordCraft's engine directly
  (`open → layout → export`) in a long-lived process: no process start, font scan or engine
  setup per document, in-memory conversions without temporary files, and structured diagnostics.
- **Built from source, not a fork.** [`scripts/build.sh`](scripts/build.sh) clones WordCraft at
  the tag in `WORDCRAFT_VERSION`, applies the (currently empty) [`patches/`](patches/), builds
  with *WordCraft's own `Cargo.lock`* so every dependency is the version WordCraft tested, and
  embeds the font set WordCraft's releases embed.
- **Process isolation (.NET / Java).** The SDKs load no native code into the host process — no
  P/Invoke, no JNI. A crash, abort or hang in the engine kills one worker process; the call
  returns a failed result, and the pool starts a fresh worker for the next conversion. The host
  runtime is never affected. Workers also exit by themselves when the host goes away (stdin
  closes).
- **Same worker, multiple SDKs.** Both SDKs speak the same [protocol](docs/protocol.md) to the
  same `wcpdf_worker`.

### Crash isolation, in layers

1. **Engine:** WordCraft is written to never panic on hostile input (no `unwrap`, bounded
   recursion, clamped sizes) and forbids `unsafe`.
2. **Core:** every conversion runs under `catch_unwind`; an escaped panic becomes error 99
   instead of a dead process. The worker's conversion thread has a 256 MB stack, so deeply nested
   documents don't overflow it.
3. **Process:** what can't be caught (abort, stack overflow, out of memory, a kill) ends the
   worker process only. The SDK sees end-of-stream, reports `WorkerCrashed`, and replaces it.
4. **Time:** a conversion over the timeout gets its worker killed (`Timeout`); cancellation kills
   the worker too, since the engine can't be interrupted mid-layout.

### Performance

- **Warm workers:** `init` loads custom fonts, scans the system fonts and runs one tiny
  conversion, so the first real document is as fast as the rest. Workers persist across
  conversions.
- **Idle-first dispatch:** a request goes to an idle worker; workers start lazily up to the
  configured maximum (default: CPU count), so a burst scales out and an idle service costs one
  process.
- **Zero temporary files** in buffer mode: the document and the PDF travel as length-prefixed
  binary frames over the pipes.
- **Build:** fat LTO, one codegen unit, `mimalloc` as the worker's allocator (measured +9–17 %
  throughput and tighter tail latency than the system allocator on the test documents).

Measured on the test fixtures (macOS, Apple Silicon, one warm worker, buffer mode, from
[`tests/worker_smoke.py`](tests/worker_smoke.py)):

| Document | Pages | p50 |
|---|---|---|
| barcode_font.docx | 1 | 2.6 ms |
| rich_formatting.docx | 2 | 4–6 ms |
| unicode_text.docx (CJK, Arabic, Hebrew, Thai) | 1 | 4.5 ms |
| stress_test.docx | 14 | 21 ms |
| large_document.docx | 28 | ~50 ms |
| **All fixtures** | | **~105 conversions/s, ~700 pages/s per worker** |

---

## .NET SDK

Targets `netstandard2.0` (.NET Framework 4.6.1+) and `net8.0`. Full guide:
[`dotnet/WordCraftPdf/README.md`](dotnet/WordCraftPdf/README.md).

### Installation

The packages are attached to every [GitHub Release](https://github.com/mapo80/wordcraft-to-pdf/releases)
(add the folder you download them to as a local NuGet source):

```xml
<PackageReference Include="WordCraftPdf" Version="0.1.0" />
<!-- the worker for your platform (x64 and arm64 in each) -->
<PackageReference Include="WordCraftPdf.NativeAssets.Linux" Version="0.1.0"
    Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
<PackageReference Include="WordCraftPdf.NativeAssets.macOS" Version="0.1.0"
    Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
<PackageReference Include="WordCraftPdf.NativeAssets.Windows" Version="0.1.0"
    Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
```

### Usage

```csharp
using WordCraftPdf;

await using var converter = await PdfConverter.CreateAsync(new PdfConverterOptions
{
    FontDirectories = new[] { "/app/fonts" },
    MaxWorkers = 4,                         // default: processor count, started on demand
    ConversionTimeout = TimeSpan.FromMinutes(2),
    WarmUp = true                           // start every worker now
});

// File to file: only paths cross the pipe
var result = await converter.ConvertAsync("input.docx", "output.pdf");
result.ThrowIfFailed();
Console.WriteLine($"{result.PageCount} pages");

// Bytes to bytes (format sniffed for DOCX, DOC, ODT, RTF), no temporary files
var pdf = await converter.ConvertAsync(docxBytes);
byte[] bytes = pdf.Data!;

// Stream to stream (ASP.NET)
await converter.ConvertAsync(Request.Body, Response.Body, DocumentFormat.Docx);

// Options
await converter.ConvertAsync("report.docx", "report.pdf", new ConversionOptions
{
    PageRange = "1-3,5", TaggedPdf = true, UpdateFields = true, Title = "Quarterly report"
});

foreach (var d in result.Diagnostics)
    Console.WriteLine(d);   // [Warning:Font] font "Calibri" is not available; substituted with "Carlito"
```

Failures are returned, not thrown (`Success`, `ErrorCode`, `ErrorMessage`, or
`ThrowIfFailed()`): a crashed worker gives `WorkerCrashed`, a timeout `Timeout` — both replace
the worker; cancelling the token kills the worker and throws `OperationCanceledException`.

---

## Java SDK

Java 8+, one runtime dependency (Gson). The release JAR bundles the workers for all six
platforms and extracts the right one on first use (cached, hash-checked). No JNI.

### Installation

Download `wordcraftpdf-<version>.jar` and its `.pom` from the
[GitHub Release](https://github.com/mapo80/wordcraft-to-pdf/releases) and install them locally:

```bash
mvn install:install-file -Dfile=wordcraftpdf-0.1.0.jar -DpomFile=wordcraftpdf-0.1.0.pom
```

```xml
<dependency>
    <groupId>com.wordcraftpdf</groupId>
    <artifactId>wordcraftpdf</artifactId>
    <version>0.1.0</version>
</dependency>
```

### Usage

```java
import com.wordcraftpdf.*;

PdfConverterOptions options = PdfConverterOptions.builder()
        .fontDirectories(Arrays.asList("/app/fonts"))
        .maxWorkers(4)                                   // default: available processors
        .conversionTimeout(2, TimeUnit.MINUTES)
        .warmUp(true)
        .build();

try (PdfConverter converter = PdfConverter.create(options)) {
    ConversionResult result = converter.convert("input.docx", "output.pdf");
    if (!result.isSuccess())
        System.err.println(result.getErrorCode() + ": " + result.getErrorMessage());

    byte[] pdf = converter.convert(docxBytes).getData();                 // sniffed format
    converter.convert(in, out, DocumentFormat.DOCX);                     // streams
    CompletableFuture<ConversionResult> f = converter.convertAsync("a.docx", "a.pdf");

    converter.convert("report.docx", "report.pdf", ConversionOptions.builder()
            .pageRange("1-3,5").taggedPdf(true).updateFields(true).build());
}
```

A crashed worker gives `WORKER_CRASHED`, a timeout `TIMEOUT` (the worker is destroyed with its
descendants), interruption kills the worker and restores the interrupt flag. All SDK threads are
daemon threads.

### Worker lookup order (both SDKs)

1. explicit option (`WorkerPath` / `workerPath`)
2. `wcpdf.worker.path` system property (Java) / `WCPDF_WORKER_PATH` environment variable
3. the packaged worker (`runtimes/<rid>/native/` in .NET, the JAR's bundled copy in Java)
4. next to the application / JAR
5. `output/bin/wcpdf_worker` in a parent directory (development checkout)

### .NET vs Java

| | .NET | Java |
|---|---|---|
| Async | `async`/`await`, `CancellationToken` | sync + `CompletableFuture` |
| Packaging | `WordCraftPdf` + `NativeAssets.{Linux,macOS,Windows}` | one JAR, all platforms |
| Cancellation | kills the worker, throws `OperationCanceledException` | interrupt kills the worker |
| Tests | 225 per TFM (net8.0, net10.0), 45 against the real worker | 157, 33 against the real worker |

---

## C API

`libwcpdf` runs the engine in process (no worker). Conversions are thread-safe and may run
concurrently; error messages and diagnostics are per calling thread. All strings are UTF-8,
paths included, on every platform.

```c
#include "wcpdf.h"

const char* fonts[] = { "/opt/fonts" };
WcPdfHandle h = wcpdf_init(fonts, 1);

WcPdfOptions opts = { 0 };          /* zero = defaults */
opts.page_range = "1-3";
opts.tagged_pdf = 1;

WcPdfError err = wcpdf_convert_file(h, "in.docx", "out.pdf", WCPDF_FORMAT_UNKNOWN, &opts);
if (err != WCPDF_OK)
    fprintf(stderr, "error %d: %s\n", err, wcpdf_get_error_message());
else
    printf("%zu pages, diagnostics: %s\n", wcpdf_get_page_count(), wcpdf_get_diagnostics_json());

uint8_t* pdf; size_t pdf_size;
err = wcpdf_convert_buffer(h, docx, docx_size, WCPDF_FORMAT_DOCX, NULL, &pdf, &pdf_size);
if (err == WCPDF_OK) { /* use pdf */ wcpdf_free_buffer(pdf); }

wcpdf_destroy(h);
```

See [`wcpdf.h`](native/wcpdf-capi/include/wcpdf.h) for the full API. In process means no crash
isolation: a fault in the engine is a fault in your process. Use a worker (or the SDKs) when
converting untrusted documents.

### Options

| Option | Default | |
|---|---|---|
| `page_range` | all | 1-based pages, e.g. `"1-3,5,8-"` |
| `password` | — | open encrypted DOCX/DOC |
| `tagged_pdf` | off | structure tree for accessibility |
| `update_fields` | off | refresh TOC, page references… before export |
| `include_markup` | off | tracked changes shown as markup |
| `include_comments` | off | with markup: comments in balloons beside each page |
| `title`, `author` | document's | PDF metadata |

### Error codes

| Code | Name | | Code | Name |
|---|---|---|---|---|
| 1 | INIT_FAILED | | 9 | NOT_INIT |
| 2 | LOAD_FAILED | | 10 | INVALID_ARGUMENT |
| 3 | EXPORT_FAILED | | 11 | PASSWORD_REQUIRED |
| 4 | INVALID_FORMAT | | 12 | WRONG_PASSWORD |
| 5 | FILE_NOT_FOUND | | 99 | UNKNOWN (incl. caught engine panics) |
| 6 | OUT_OF_MEMORY | | 100 | WORKER_CRASHED (SDKs) |
| 7 | PERMISSION_DENIED | | 101 | TIMEOUT (SDKs) |

---

## Fonts

- **Embedded:** WordCraft's bundled Latin families plus the
  [craft-fonts](https://github.com/storytold/craft-fonts) set WordCraft's releases embed
  (Japanese, Simplified Chinese, Korean, Arabic, Hebrew, Thai, Devanagari, Georgian, symbols…),
  so text in those scripts renders on a bare server. Build with `WITH_CRAFT_FONTS=0` for a smaller
  worker that relies on installed fonts.
- **Installed:** the system font folders are scanned (`/usr/share/fonts`, `~/.fonts`,
  `/Library/Fonts`, `C:\Windows\Fonts`…). No fontconfig needed.
- **Custom:** font directories passed at init (`FontDirectories` / `fontDirectories` /
  `wcpdf_init`) are loaded first, subfolders included.
- **Substitution:** a font the document asks for that isn't available is replaced
  (metric-compatible Carlito for Calibri, Caladea for Cambria, Liberation for Arial / Times New
  Roman / Courier New, then similar system fonts) and reported as a diagnostic:
  `font "Calibri" is not available; substituted with "Carlito"`.

On Linux servers, installing `fonts-crosextra-carlito fonts-crosextra-caladea fonts-liberation2`
(Debian/Ubuntu) gives Word documents their intended line breaks.

---

## Building from source

Requirements: Rust 1.95+, git, bash (Git Bash on Windows, with the MSVC toolchain), Python 3 for
the gate. No system libraries.

```bash
./scripts/build.sh          # clone WordCraft at WORDCRAFT_VERSION, patch, build, assemble output/
./scripts/run-gate.sh       # C API tests + worker protocol tests + throughput
```

| Variable | Default | |
|---|---|---|
| `WC_SRC_DIR` | `wc-src/` | WordCraft checkout (cloned when missing) |
| `OUTPUT_DIR` | `output/` | artifacts |
| `TARGET` | host | Rust target triple to cross-compile for |
| `WITH_CRAFT_FONTS` | `1` | embed craft-fonts (`0`: installed fonts only) |
| `CRAFT_FONTS_DIR` | — | use this craft-fonts checkout instead of the pinned one |
| `NPROC` | CPU count | parallel jobs |
| `CLEAN_BUILD` | `0` | wipe `target/` and the output first |

A fresh build (clone included) takes about 5 minutes on an Apple M-series laptop.

### Output layout

```
output/
├── bin/wcpdf_worker[.exe]          # the worker the SDKs run
├── lib/libwcpdf.{so,dylib,a}       # C API (wcpdf.dll + wcpdf.lib on Windows)
├── include/wcpdf.h
├── licenses/                       # WordCraft (MIT/Apache-2.0) and font licences
└── build-metadata.json             # WordCraft tag + commit, craft-fonts commit, target, sizes
```

### Docker (Linux)

```bash
./scripts/docker-build.sh                       # build in a container, extract to output-linux-<arch>/
PLATFORM=linux/amd64 ./scripts/linux-docker-validate.sh   # build + gate inside a container
```

The runtime image needs nothing beyond glibc; it optionally adds the metric-compatible fonts
above (`INSTALL_FONTS=1`, default).

### Updating WordCraft

```bash
./scripts/bump-wordcraft-version.sh v0.13.0     # updates WORDCRAFT_VERSION and the Krilla patch
rm -rf wc-src && ./scripts/build.sh && ./scripts/run-gate.sh
```

### How patching works

Like SlimLO's LibreOffice patches, WordCraftPdf modifies vanilla WordCraft only through
[`patches/`](patches/): idempotent `*.sh` scripts (run with the source directory as `$1`) and
`*.patch` files (skipped when already applied), applied by
[`scripts/apply-patches.sh`](scripts/apply-patches.sh) on every build. WordCraft builds as-is
today, so there are none.

Two things are kept in step with WordCraft automatically: `Cargo.lock` is copied from the
checkout on every build, and the build fails if the Krilla `[patch.crates-io]` entry WordCraft
requires differs from ours (`bump-wordcraft-version.sh` updates it).

---

## Project structure

```
wordcraft-to-pdf/
├── WORDCRAFT_VERSION              # pinned WordCraft tag
├── Cargo.toml                     # workspace: path deps into wc-src/, Krilla patch, release profile
├── native/
│   ├── wcpdf-core/                # conversion, options, diagnostics, fonts (safe Rust)
│   ├── wcpdf-capi/                # libwcpdf + include/wcpdf.h
│   └── wcpdf-worker/              # wcpdf_worker (protocol in docs/protocol.md)
├── dotnet/                        # .NET SDK, native-asset packages, tests
├── java/                          # Java SDK, tests, example
├── patches/                       # modifications to vanilla WordCraft (none today)
├── scripts/                       # build, patch, bump, gate, docker, packaging
├── tests/                         # fixtures, C API test, worker protocol/throughput test
├── docker/                        # Linux build/runtime/gate images
└── docs/protocol.md               # worker protocol
```

## CI and releases

| Workflow | Trigger | What it does |
|---|---|---|
| [`build.yml`](.github/workflows/build.yml) | push / PR to `main`, manual | native build + gate on 6 platforms (Linux in a Debian bookworm container for glibc 2.36 compatibility), .NET and Java test suites on all 6 against the fresh workers, Linux Docker validation; uploads `native-<rid>` artifacts and gate reports |
| [`release.yml`](.github/workflows/release.yml) | tag `v*.*.*`, manual (version input) | runs the build, then creates the GitHub Release with the NuGet packages, the Maven JAR (all platforms) with sources/javadoc/POM, per-platform native archives and `SHA256SUMS`; publishes to nuget.org / Maven Central only when their secrets are set |

| Platform | Runner |
|---|---|
| linux-x64 / linux-arm64 | `ubuntu-latest` / `ubuntu-24.04-arm` (bookworm container) |
| osx-arm64 / osx-x64 | `macos-14` / `macos-15-intel` |
| win-x64 / win-arm64 | `windows-2022` / `windows-11-arm` |

To cut a release: bump `version` in `Cargo.toml`, `java/pom.xml` and the .NET projects if
needed, push, wait for **Build** to pass, then `git tag v0.1.0 && git push origin v0.1.0`.

---

## License

[MPL-2.0](LICENSE). WordCraft is MIT OR Apache-2.0 and the embedded fonts are under the SIL Open
Font License; their licence texts ship in `output/licenses/` and in every package.
