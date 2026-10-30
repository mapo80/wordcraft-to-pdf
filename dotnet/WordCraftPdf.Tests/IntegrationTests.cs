using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using static WordCraftPdf.Tests.TestHelpers;

namespace WordCraftPdf.Tests;

/// <summary>
/// Shared converter over the real wcpdf_worker (output/bin/ in this checkout, or
/// WCPDF_WORKER_PATH). Tests skip when the worker or the fixtures aren't there.
/// </summary>
public sealed class RealWorkerFixture : IAsyncLifetime
{
    public PdfConverter? Converter { get; private set; }

    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        if (RealWorkerPath is null)
        {
            SkipReason = "wcpdf_worker not found (build it with scripts/build.sh or set WCPDF_WORKER_PATH)";
            return;
        }
        if (FixturesDir is null)
        {
            SkipReason = "tests/fixtures not found";
            return;
        }
        Converter = await PdfConverter.CreateAsync(new PdfConverterOptions { MaxWorkers = 4 });
    }

    public async Task DisposeAsync()
    {
        if (Converter is not null)
            await Converter.DisposeAsync();
    }
}

public class IntegrationTests : IClassFixture<RealWorkerFixture>
{
    private readonly RealWorkerFixture _fixture;

    public IntegrationTests(RealWorkerFixture fixture) => _fixture = fixture;

    private PdfConverter Converter
    {
        get
        {
            Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
            return _fixture.Converter!;
        }
    }

    private static string Fixture(string name) => Path.Combine(FixturesDir!, name);

    public static IEnumerable<object[]> Fixtures => FixtureNames();

    // ---------------------------------------------------------------- every fixture, every mode

    [SkippableTheory]
    [MemberData(nameof(Fixtures))]
    public async Task File_ConvertsFixture(string name)
    {
        var converter = Converter;
        var dir = NewTempDir();
        try
        {
            var output = Path.Combine(dir, Path.ChangeExtension(name, ".pdf"));
            var result = await converter.ConvertAsync(Fixture(name), output);
            Assert.True(result.Success, $"{result.ErrorCode}: {result.ErrorMessage}");
            Assert.True(result.PageCount >= 1);
            Assert.True(IsPdf(File.ReadAllBytes(output)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Bytes_ConvertsFixture_WithSniffedAndExplicitFormat(string name)
    {
        var converter = Converter;
        var bytes = File.ReadAllBytes(Fixture(name));

        var sniffed = await converter.ConvertAsync(bytes);
        Assert.True(sniffed.Success, $"{sniffed.ErrorCode}: {sniffed.ErrorMessage}");
        Assert.True(IsPdf(sniffed.Data));
        Assert.True(sniffed.PageCount >= 1);

        var explicitFormat = await converter.ConvertAsync(bytes, DocumentFormat.Docx);
        Assert.True(explicitFormat.Success, explicitFormat.ErrorMessage);
        Assert.Equal(sniffed.PageCount, explicitFormat.PageCount);
    }

    [SkippableTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Streams_ConvertFixture(string name)
    {
        var converter = Converter;
        var dir = NewTempDir();
        try
        {
            // FileStream (seekable) → MemoryStream
            var output = new MemoryStream();
            using (var input = File.OpenRead(Fixture(name)))
            {
                var result = await converter.ConvertAsync(input, output, DocumentFormat.Docx);
                Assert.True(result.Success, result.ErrorMessage);
                Assert.True(result.PageCount >= 1);
            }
            Assert.True(IsPdf(output.ToArray()));

            // MemoryStream → file
            var pdfPath = Path.Combine(dir, "out.pdf");
            var r2 = await converter.ConvertAsync(new MemoryStream(File.ReadAllBytes(Fixture(name))), pdfPath);
            Assert.True(r2.Success, r2.ErrorMessage);
            Assert.True(IsPdf(File.ReadAllBytes(pdfPath)));

            // file → stream
            var output3 = new MemoryStream();
            var r3 = await converter.ConvertAsync(Fixture(name), output3);
            Assert.True(r3.Success, r3.ErrorMessage);
            Assert.True(IsPdf(output3.ToArray()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- other formats

    [SkippableTheory]
    [InlineData(DocumentFormat.Markdown, "# Title\n\nSome *markdown* text.\n\n- one\n- two\n")]
    [InlineData(DocumentFormat.Html, "<html><body><h1>Title</h1><p>Some <b>HTML</b>.</p></body></html>")]
    [InlineData(DocumentFormat.Text, "Plain text line 1\nPlain text line 2\n")]
    [InlineData(DocumentFormat.Rtf, "{\\rtf1\\ansi{\\fonttbl\\f0\\fswiss Helvetica;}\\f0\\pard Hello {\\b RTF}.\\par}")]
    public async Task Bytes_ConvertsOtherFormats(DocumentFormat format, string content)
    {
        var converter = Converter;
        var result = await converter.ConvertAsync(Encoding.UTF8.GetBytes(content), format);
        Assert.True(result.Success, $"{result.ErrorCode}: {result.ErrorMessage}");
        Assert.True(IsPdf(result.Data));
        Assert.Equal(1, result.PageCount);
    }

    [SkippableFact]
    public async Task File_DetectsFormatFromExtension()
    {
        var converter = Converter;
        var dir = NewTempDir();
        try
        {
            var md = Path.Combine(dir, "notes.md");
            File.WriteAllText(md, "# Notes\n\nHello.\n");
            var result = await converter.ConvertAsync(md, Path.Combine(dir, "notes.pdf"));
            Assert.True(result.Success, result.ErrorMessage);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- options

    [SkippableFact]
    public async Task PageRange_SelectsPages()
    {
        var converter = Converter;
        var bytes = File.ReadAllBytes(Fixture("large_document.docx"));
        var all = await converter.ConvertAsync(bytes);
        Assert.True(all.Success, all.ErrorMessage);
        Skip.If(all.PageCount < 2, "large_document.docx has a single page");

        var first = await converter.ConvertAsync(bytes, options: new ConversionOptions { PageRange = "1" });
        Assert.True(first.Success, first.ErrorMessage);
        Assert.Equal(1, first.PageCount);

        var fromTwo = await converter.ConvertAsync(bytes, options: new ConversionOptions { PageRange = "2-" });
        Assert.True(fromTwo.Success, fromTwo.ErrorMessage);
        Assert.Equal(all.PageCount - 1, fromTwo.PageCount);
    }

    [SkippableTheory]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("3-1")]
    [InlineData("9999")]
    public async Task PageRange_Invalid_ReturnsInvalidArgument(string range)
    {
        var converter = Converter;
        var result = await converter.ConvertAsync(File.ReadAllBytes(Fixture("multi_font.docx")),
            options: new ConversionOptions { PageRange = range });
        Assert.False(result.Success);
        Assert.Equal(WordCraftPdfErrorCode.InvalidArgument, result.ErrorCode);
    }

    [SkippableFact]
    public async Task AllOptions_AreAccepted()
    {
        var converter = Converter;
        var result = await converter.ConvertAsync(File.ReadAllBytes(Fixture("rich_formatting.docx")),
            options: new ConversionOptions
            {
                TaggedPdf = true, UpdateFields = true, IncludeMarkup = true, IncludeComments = true,
                Title = "Custom title", Author = "Custom author", PageRange = "1-"
            });
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("Custom title", Encoding.Latin1.GetString(result.Data!).Replace("\0", ""));
    }

    // ---------------------------------------------------------------- invalid input

    [SkippableFact]
    public async Task InvalidInput_FailsWithoutCrashingTheWorker()
    {
        var converter = Converter;
        var garbage = Encoding.ASCII.GetBytes("this is definitely not a word processing document");

        var sniffed = await converter.ConvertAsync(garbage);
        Assert.False(sniffed.Success);
        Assert.NotEqual(WordCraftPdfErrorCode.WorkerCrashed, sniffed.ErrorCode);
        Assert.False(string.IsNullOrEmpty(sniffed.ErrorMessage));

        var corruptDocx = new byte[] { (byte)'P', (byte)'K', 3, 4 }.Concat(Enumerable.Repeat((byte)0x41, 500)).ToArray();
        var asDocx = await converter.ConvertAsync(corruptDocx, DocumentFormat.Docx);
        Assert.False(asDocx.Success);
        Assert.NotEqual(WordCraftPdfErrorCode.WorkerCrashed, asDocx.ErrorCode);

        Assert.True(await converter.ConvertAsync(File.ReadAllBytes(Fixture("multi_font.docx"))));
    }

    [SkippableFact]
    public async Task InvalidInput_FileMode()
    {
        var converter = Converter;
        var dir = NewTempDir();
        try
        {
            var bad = Path.Combine(dir, "bad.docx");
            File.WriteAllText(bad, "not a zip");
            var result = await converter.ConvertAsync(bad, Path.Combine(dir, "bad.pdf"));
            Assert.False(result.Success);
            Assert.NotEqual(WordCraftPdfErrorCode.WorkerCrashed, result.ErrorCode);
            Assert.False(File.Exists(Path.Combine(dir, "bad.pdf")));

            var unknownExtension = Path.Combine(dir, "file.xyz");
            File.WriteAllText(unknownExtension, "whatever");
            var r2 = await converter.ConvertAsync(unknownExtension, Path.Combine(dir, "x.pdf"));
            Assert.False(r2.Success);
            Assert.Equal(WordCraftPdfErrorCode.InvalidFormat, r2.ErrorCode);

            var r3 = await converter.ConvertAsync(Fixture("multi_font.docx"), Path.Combine(dir, "no", "such", "dir", "out.pdf"));
            Assert.False(r3.Success);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [SkippableFact]
    public async Task HtmlWithoutExplicitFormat_IsNotSniffed()
    {
        var converter = Converter;
        var result = await converter.ConvertAsync(Encoding.UTF8.GetBytes("<html><body>hi</body></html>"));
        Assert.False(result.Success);
        Assert.Equal(WordCraftPdfErrorCode.InvalidFormat, result.ErrorCode);
    }

    // ---------------------------------------------------------------- misc

    [SkippableFact]
    public void Version_IsReported()
    {
        var converter = Converter;
        Assert.NotNull(converter.Version);
        Assert.Contains("WordCraft", converter.Version);
    }

    [SkippableFact]
    public async Task MissingFonts_AreReportedAsDiagnostics()
    {
        var converter = Converter;
        var result = await converter.ConvertAsync(File.ReadAllBytes(Fixture("missing_fonts.docx")));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.HasFontWarnings, "missing_fonts.docx should report substitutions");
        Assert.Contains(result.Diagnostics, d => d.Category == DiagnosticCategory.Font && d.FontName != null && d.SubstitutedWith != null);
    }
}

/// <summary>Real-worker tests that need their own converter (fonts, crash, timeout, recycle, concurrency).</summary>
public class IntegrationIsolationTests
{
    private static void SkipIfUnavailable()
    {
        Skip.If(RealWorkerPath is null, "wcpdf_worker not found (build it with scripts/build.sh or set WCPDF_WORKER_PATH)");
        Skip.If(FixturesDir is null, "tests/fixtures not found");
    }

    private static string Fixture(string name) => Path.Combine(FixturesDir!, name);

    /// <summary>A Markdown document big enough to keep a worker busy for a while.</summary>
    private static byte[] BigMarkdown(int paragraphs)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < paragraphs; i++)
            sb.Append("## Section ").Append(i).Append("\n\nLorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. **Bold** and *italic* text.\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    [SkippableFact]
    public async Task FontDirectories_BarcodeFontIsNotSubstituted()
    {
        SkipIfUnavailable();
        Skip.If(FontsDir is null, "tests/fixtures/fonts not found");
        await using var converter = PdfConverter.Create(new PdfConverterOptions
        {
            MaxWorkers = 1,
            FontDirectories = new[] { FontsDir! }
        });
        var result = await converter.ConvertAsync(File.ReadAllBytes(Fixture("barcode_font.docx")));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Category == DiagnosticCategory.Font
            && (d.FontName ?? d.Message).IndexOf("Barcode", StringComparison.OrdinalIgnoreCase) >= 0);
        Assert.Empty(converter.StartupDiagnostics);
    }

    [SkippableFact]
    public async Task FontDirectories_MissingFolder_IsAStartupDiagnostic()
    {
        SkipIfUnavailable();
        await using var converter = PdfConverter.Create(new PdfConverterOptions
        {
            MaxWorkers = 1,
            FontDirectories = new[] { Path.Combine(Path.GetTempPath(), "wcpdf-no-such-font-dir-" + Guid.NewGuid().ToString("N")) }
        });
        await converter.WarmUpAsync();
        Assert.NotEmpty(converter.StartupDiagnostics);
        Assert.True(await converter.ConvertAsync(File.ReadAllBytes(Fixture("multi_font.docx"))));
    }

    [SkippableFact]
    public async Task Concurrency_16Conversions_4Workers_AllSucceed()
    {
        SkipIfUnavailable();
        await using var converter = PdfConverter.Create(new PdfConverterOptions { MaxWorkers = 4 });
        var names = FixtureNames().Select(o => (string)o[0]).ToArray();
        var tasks = Enumerable.Range(0, 16).Select(i =>
            converter.ConvertAsync(File.ReadAllBytes(Fixture(names[i % names.Length])))).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r.Success, $"{r.ErrorCode}: {r.ErrorMessage}"));
        Assert.All(results, r => Assert.True(IsPdf(r.Data)));
        Assert.InRange(converter.Pool.LiveWorkerCount, 1, 4);
    }

    [SkippableFact]
    public async Task CrashIsolation_KilledWorker_ReturnsWorkerCrashed_HostSurvives_NextSucceeds()
    {
        SkipIfUnavailable();
        await using var converter = await PdfConverter.CreateAsync(new PdfConverterOptions { MaxWorkers = 1, WarmUp = true });
        var doc = BigMarkdown(4000);

        ConversionResult<byte[]>? crashed = null;
        for (int attempt = 0; attempt < 5 && crashed is null; attempt++)
        {
            var conversion = converter.ConvertAsync(doc, DocumentFormat.Markdown);
            Assert.True(await WaitUntilAsync(() => converter.Pool.Workers.Any(w => w.IsBusy) || conversion.IsCompleted, TimeSpan.FromSeconds(10)));
            var busy = converter.Pool.Workers.FirstOrDefault(w => w.IsBusy);
            if (busy is not null)
            {
                using var p = Process.GetProcessById(busy.ProcessId);
                p.Kill();
            }
            var result = await conversion;
            if (!result.Success)
                crashed = result; // otherwise the conversion beat the kill: try again
        }

        Assert.NotNull(crashed);
        Assert.Equal(WordCraftPdfErrorCode.WorkerCrashed, crashed!.ErrorCode);
        Assert.Null(crashed.Data);

        var next = await converter.ConvertAsync(File.ReadAllBytes(Fixture("multi_font.docx")));
        Assert.True(next.Success, next.ErrorMessage);
        Assert.True(IsPdf(next.Data));
    }

    [SkippableFact]
    public async Task Timeout_TinyTimeout_ReturnsTimeout_NextConversionSucceeds()
    {
        SkipIfUnavailable();
        await using var converter = await PdfConverter.CreateAsync(new PdfConverterOptions { MaxWorkers = 1, WarmUp = true });
        converter.Pool.ConversionTimeout = TimeSpan.FromMilliseconds(1);
        // Timer resolution is coarse on some platforms (~15 ms on Windows) and a fast machine can
        // beat it: grow the document until a conversion outlasts the timeout.
        ConversionResult<byte[]>? timedOut = null;
        int pid = 0;
        foreach (var paragraphs in new[] { 4000, 20000, 80000 })
        {
            pid = converter.Pool.Workers.Single().ProcessId;
            var result = await converter.ConvertAsync(BigMarkdown(paragraphs), DocumentFormat.Markdown);
            if (!result.Success)
            {
                timedOut = result;
                break;
            }
        }
        Assert.NotNull(timedOut);
        Assert.Equal(WordCraftPdfErrorCode.Timeout, timedOut!.ErrorCode);
        Assert.True(await WaitUntilAsync(() => !IsProcessRunning(pid), TimeSpan.FromSeconds(5)));

        converter.Pool.ConversionTimeout = TimeSpan.FromMinutes(2);
        var next = await converter.ConvertAsync(File.ReadAllBytes(Fixture("multi_font.docx")));
        Assert.True(next.Success, next.ErrorMessage);
    }

    [SkippableFact]
    public async Task Cancellation_KillsWorker_NextConversionSucceeds()
    {
        SkipIfUnavailable();
        await using var converter = await PdfConverter.CreateAsync(new PdfConverterOptions { MaxWorkers = 1, WarmUp = true });
        using var cts = new System.Threading.CancellationTokenSource();
        var conversion = converter.ConvertAsync(BigMarkdown(4000), DocumentFormat.Markdown, cancellationToken: cts.Token);
        cts.Cancel();
        try
        {
            await conversion;
        }
        catch (OperationCanceledException)
        {
        }
        Assert.True(await converter.ConvertAsync(File.ReadAllBytes(Fixture("multi_font.docx"))));
    }

    [SkippableFact]
    public async Task Recycle_MaxConversionsPerWorker2_FiveConversionsSucceed()
    {
        SkipIfUnavailable();
        await using var converter = PdfConverter.Create(new PdfConverterOptions { MaxWorkers = 1, MaxConversionsPerWorker = 2 });
        var bytes = File.ReadAllBytes(Fixture("unicode_text.docx"));
        var pids = new HashSet<int>();
        for (int i = 0; i < 5; i++)
        {
            var result = await converter.ConvertAsync(bytes);
            Assert.True(result.Success, $"conversion {i}: {result.ErrorCode} {result.ErrorMessage}");
            foreach (var w in converter.Pool.Workers)
                pids.Add(w.ProcessId);
        }
        Assert.Equal(1, converter.Pool.LiveWorkerCount);
        Assert.True(pids.Count >= 2, "workers should have been recycled");
    }

    [SkippableFact]
    public async Task Dispose_StopsAllWorkers()
    {
        SkipIfUnavailable();
        var converter = await PdfConverter.CreateAsync(new PdfConverterOptions { MaxWorkers = 2, WarmUp = true });
        var pids = converter.Pool.Workers.Select(w => w.ProcessId).ToArray();
        Assert.Equal(2, pids.Length);
        await converter.DisposeAsync();
        Assert.All(pids, pid => Assert.False(IsProcessRunning(pid)));
    }
}
