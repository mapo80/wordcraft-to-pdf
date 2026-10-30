// WordCraftPdf test CLI: converts a document with the SDK and reports timing, pages and
// diagnostics. Useful as a smoke test of a deployment and as a quick benchmark.
//
// Usage: WordCraftPdf.TestCli <input> [options]
//   -o, --output <file>     output PDF (default: <input>.pdf; with --repeat, only the last is kept)
//   --mode file|bytes|stream conversion mode (default: bytes)
//   --format <name>         docx, doc, odt, rtf, html, markdown, text (default: from extension / sniffed)
//   --fonts <dir>           extra font folder (repeatable)
//   --pages <range>         page range, e.g. 1-3,5
//   --tagged                tagged PDF
//   --workers <n>           MaxWorkers (default: processor count)
//   --repeat <n>            number of conversions (default: 1)
//   --parallel <n>          conversions in flight at once (default: 1)
//   --timeout <seconds>     ConversionTimeout
//   --worker <path>         wcpdf_worker path (default: automatic search)
//
// Exit codes: 0 success, 1 bad arguments / input not found, 2 conversion failed, 3 exception.
using System.Diagnostics;
using WordCraftPdf;

string? input = null, output = null, pages = null, workerPath = null;
var mode = "bytes";
var format = DocumentFormat.Unknown;
var fonts = new List<string>();
bool tagged = false;
int workers = Environment.ProcessorCount, repeat = 1, parallel = 1;
TimeSpan? timeout = null;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
        switch (args[i])
        {
            case "-o" or "--output": output = Next(); break;
            case "--mode": mode = Next(); break;
            case "--format": format = Enum.Parse<DocumentFormat>(Next(), ignoreCase: true); break;
            case "--fonts": fonts.Add(Next()); break;
            case "--pages": pages = Next(); break;
            case "--tagged": tagged = true; break;
            case "--workers": workers = int.Parse(Next()); break;
            case "--repeat": repeat = int.Parse(Next()); break;
            case "--parallel": parallel = int.Parse(Next()); break;
            case "--timeout": timeout = TimeSpan.FromSeconds(double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture)); break;
            case "--worker": workerPath = Next(); break;
            case "-h" or "--help":
                Console.Error.WriteLine("Usage: WordCraftPdf.TestCli <input> [-o out.pdf] [--mode file|bytes|stream] [--format f] [--fonts dir] [--pages r] [--tagged] [--workers n] [--repeat n] [--parallel n] [--timeout s] [--worker path]");
                return 0;
            default:
                if (input is null && !args[i].StartsWith('-'))
                    input = args[i];
                else
                    throw new ArgumentException($"unknown argument {args[i]}");
                break;
        }
    }
    if (mode is not ("file" or "bytes" or "stream"))
        throw new ArgumentException("--mode must be file, bytes or stream");
    if (repeat < 1 || parallel < 1)
        throw new ArgumentException("--repeat and --parallel must be at least 1");
}
catch (Exception ex) when (ex is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

input ??= "test.docx";
if (!File.Exists(input))
{
    Console.Error.WriteLine($"File not found: {input}");
    return 1;
}
output ??= Path.ChangeExtension(input, ".pdf");

try
{
    var startup = Stopwatch.StartNew();
    await using var converter = await PdfConverter.CreateAsync(new PdfConverterOptions
    {
        WorkerPath = workerPath,
        MaxWorkers = workers,
        FontDirectories = fonts.Count > 0 ? fonts : null,
        ConversionTimeout = timeout ?? TimeSpan.FromMinutes(5),
        WarmUp = true
    });
    startup.Stop();
    Console.Error.WriteLine($"Worker: {converter.Version} ({workers} worker(s) ready in {startup.ElapsedMilliseconds} ms)");
    foreach (var d in converter.StartupDiagnostics)
        Console.Error.WriteLine($"  startup {d}");

    var options = new ConversionOptions { PageRange = pages, TaggedPdf = tagged };
    var inputBytes = mode == "bytes" ? await File.ReadAllBytesAsync(input) : null;
    Console.Error.WriteLine($"Input: {input} ({new FileInfo(input).Length} bytes), mode {mode}, {repeat} conversion(s), {parallel} in parallel");

    async Task<(ConversionResult Result, long Bytes, double Ms)> ConvertOnce()
    {
        var sw = Stopwatch.StartNew();
        switch (mode)
        {
            case "file":
            {
                var r = await converter.ConvertAsync(input, output, options);
                return (r, r.Success ? new FileInfo(output).Length : 0, sw.Elapsed.TotalMilliseconds);
            }
            case "stream":
            {
                await using var src = File.OpenRead(input);
                var dst = new MemoryStream();
                var r = await converter.ConvertAsync(src, dst, format, options);
                if (r.Success)
                    await File.WriteAllBytesAsync(output, dst.ToArray());
                return (r, dst.Length, sw.Elapsed.TotalMilliseconds);
            }
            default:
            {
                var r = await converter.ConvertAsync(inputBytes!, format, options);
                if (r.Success)
                    await File.WriteAllBytesAsync(output, r.Data!);
                return (r, r.Data?.Length ?? 0, sw.Elapsed.TotalMilliseconds);
            }
        }
    }

    var total = Stopwatch.StartNew();
    var timings = new List<double>();
    ConversionResult? last = null;
    long lastBytes = 0;
    int failures = 0;
    using var gate = new SemaphoreSlim(parallel);
    var tasks = Enumerable.Range(0, repeat).Select(async _ =>
    {
        await gate.WaitAsync();
        try
        {
            var (r, bytes, ms) = await ConvertOnce();
            lock (timings)
            {
                timings.Add(ms);
                last = r;
                lastBytes = bytes;
                if (!r.Success)
                {
                    failures++;
                    Console.Error.WriteLine($"FAILED: {r.ErrorCode}: {r.ErrorMessage}");
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }).ToArray();
    await Task.WhenAll(tasks);
    total.Stop();

    if (last is { Success: true })
    {
        Console.Error.WriteLine($"OK: {last.PageCount} page(s), {lastBytes} bytes PDF -> {output}");
        foreach (var d in last.Diagnostics)
            Console.Error.WriteLine($"  {d}");
    }
    timings.Sort();
    Console.Error.WriteLine(
        $"Time: total {total.ElapsedMilliseconds} ms, per conversion min {timings[0]:F1} / median {timings[timings.Count / 2]:F1} / max {timings[^1]:F1} ms, " +
        $"{repeat / total.Elapsed.TotalSeconds:F1} conversions/s");
    return failures == 0 ? 0 : 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"EXCEPTION: {ex}");
    return 3;
}
