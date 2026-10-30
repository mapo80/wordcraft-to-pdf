# WordCraftPdf

**Managed .NET library for document-to-PDF conversion with the WordCraft engine.**

WordCraftPdf converts DOCX, DOC, ODT, RTF, HTML, Markdown and plain text to PDF. The engine
(pure Rust) runs in a pool of persistent `wcpdf_worker` processes that the library talks to
over pipes: no native code is loaded into your process, so a crash or hang in the engine can
never take your application down.

## Features

- **Process isolation** — a worker crash fails one conversion (`WorkerCrashed`) and the worker
  is replaced; a hung conversion is killed after `ConversionTimeout` (`Timeout`)
- **Fast** — persistent, pre-initialised workers; each request goes to an idle worker; buffer
  mode streams bytes over the pipe without temporary files or extra copies
- **Thread-safe** — up to `MaxWorkers` (default: processor count) conversions in parallel
- **Three modes** — file to file (the worker reads and writes the files), bytes to bytes, streams
- **Font diagnostics** — substitutions reported as structured `ConversionDiagnostic` objects;
  custom font folders via `FontDirectories`
- **Options** — page ranges, tagged PDF, field update, tracked-changes markup and comments,
  title/author, passwords
- **Cross-platform** — Linux x64/arm64, macOS arm64/x64, Windows x64/arm64
- **Wide .NET support** — targets `netstandard2.0` and `net8.0`

## Installation

```xml
<PackageReference Include="WordCraftPdf" Version="0.1.0" />

<!-- The worker for your platform -->
<PackageReference Include="WordCraftPdf.NativeAssets.Linux" Version="0.1.0"
    Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
<PackageReference Include="WordCraftPdf.NativeAssets.macOS" Version="0.1.0"
    Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
<PackageReference Include="WordCraftPdf.NativeAssets.Windows" Version="0.1.0"
    Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
```

## Quick start

```csharp
using WordCraftPdf;

await using var converter = PdfConverter.Create();

var result = await converter.ConvertAsync("input.docx", "output.pdf");
result.ThrowIfFailed();
Console.WriteLine($"{result.PageCount} pages");
```

## Advanced usage

```csharp
await using var converter = await PdfConverter.CreateAsync(new PdfConverterOptions
{
    FontDirectories = new[] { "/app/fonts" },
    MaxWorkers = 4,
    MaxConversionsPerWorker = 500,
    ConversionTimeout = TimeSpan.FromMinutes(2),
    WarmUp = true
});

// Bytes to bytes; the format is sniffed for DOCX, DOC, ODT and RTF
byte[] docx = await File.ReadAllBytesAsync("input.docx");
var pdf = await converter.ConvertAsync(docx);
if (pdf.Success)
    await File.WriteAllBytesAsync("output.pdf", pdf.Data!);
else
    Console.WriteLine($"{pdf.ErrorCode}: {pdf.ErrorMessage}");

// Stream to stream (ASP.NET)
await converter.ConvertAsync(Request.Body, Response.Body, DocumentFormat.Docx);

// Markdown needs an explicit format in buffer mode
await converter.ConvertAsync(markdownBytes, DocumentFormat.Markdown);

// Options
await converter.ConvertAsync("report.docx", "report.pdf", new ConversionOptions
{
    PageRange = "1-3,5",
    TaggedPdf = true,
    UpdateFields = true,
    Title = "Quarterly report"
});

foreach (var d in result.Diagnostics)
    Console.WriteLine(d); // [Warning:Font] font "Calibri" is not available; substituted with "Carlito"
```

## Failure behaviour

Conversion failures are returned, not thrown: check `Success` / `ErrorCode`, or call
`ThrowIfFailed()` to get a `WordCraftPdfException`.

| Situation | Outcome |
|---|---|
| Document can't be loaded / converted | failed result, protocol error code (`LoadFailed`, `InvalidFormat`, `PasswordRequired`, …) |
| Worker process dies mid-conversion | failed result, `WorkerCrashed`; the worker is replaced on the next conversion |
| Conversion exceeds `ConversionTimeout` | failed result, `Timeout`; the worker is killed and replaced |
| `CancellationToken` cancelled mid-conversion | the worker is killed, `OperationCanceledException` is thrown |
| Converter disposed | idle workers quit; running conversions get a few seconds, then their workers are killed |

## Locating the worker

`PdfConverterOptions.WorkerPath`, then the `WCPDF_WORKER_PATH` environment variable, then
`runtimes/<rid>/native/wcpdf_worker` and `wcpdf_worker` next to the application (where the
NativeAssets packages put it), then `output/bin/wcpdf_worker` in a parent directory
(development checkout).

## Architecture

Each `wcpdf_worker` process loads the fonts once, warms up, and then serves conversions one at
a time. The SDK speaks the length-prefixed JSON/binary protocol described in
`docs/protocol.md` over the worker's stdin/stdout; stderr is drained continuously and only its
last 64 KB are kept for error messages. A worker exits by itself when its stdin closes, so it
never outlives the host.

## License

MPL-2.0.
