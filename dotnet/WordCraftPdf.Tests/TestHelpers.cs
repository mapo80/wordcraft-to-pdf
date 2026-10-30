using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using WordCraftPdf.Internal;

namespace WordCraftPdf.Tests;

internal static class TestHelpers
{
    /// <summary>The fake worker built next to the tests (see WordCraftPdf.Tests.FakeWorker).</summary>
    public static string FakeWorkerPath
    {
        get
        {
            var name = "WordCraftPdf.Tests.FakeWorker" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");
            var path = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException("The fake worker was not built next to the tests", path);
            return path;
        }
    }

    /// <summary>A converter over the fake worker. Directives go in <paramref name="directives"/> (font_paths).</summary>
    public static PdfConverter CreateFake(
        int maxWorkers = 1,
        int maxConversionsPerWorker = 0,
        TimeSpan? timeout = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? shutdownTimeout = null,
        bool warmUp = false,
        params string[] directives)
    {
        return PdfConverter.Create(new PdfConverterOptions
        {
            WorkerPath = FakeWorkerPath,
            MaxWorkers = maxWorkers,
            MaxConversionsPerWorker = maxConversionsPerWorker,
            ConversionTimeout = timeout ?? TimeSpan.FromSeconds(30),
            WorkerStartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30),
            ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5),
            WarmUp = warmUp,
            FontDirectories = directives.Length > 0 ? directives : null
        });
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The worker pid embedded by the fake worker in its PDF.</summary>
    public static int FakePid(byte[] pdf)
    {
        var text = Encoding.ASCII.GetString(pdf, 0, Math.Min(pdf.Length, 64));
        var start = text.IndexOf("pid=", StringComparison.Ordinal) + 4;
        var end = text.IndexOf('\n', start);
        return int.Parse(text.Substring(start, end - start));
    }

    public static bool IsProcessRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }
        return condition();
    }

    // ---------------------------------------------------------------- real worker

    /// <summary>The real wcpdf_worker, or null when it isn't built / configured.</summary>
    public static string? RealWorkerPath
    {
        get
        {
            try
            {
                return WorkerLocator.FindWorkerExecutable();
            }
            catch
            {
                return null;
            }
        }
    }

    public static string? FindUp(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static string? FixturesDir => FindUp("tests", "fixtures");

    public static string? FontsDir => FindUp("tests", "fixtures", "fonts");

    public static IEnumerable<object[]> FixtureNames() => new[]
    {
        "barcode_font.docx", "large_document.docx", "missing_fonts.docx", "multi_font.docx",
        "rich_formatting.docx", "stress_test.docx", "unicode_text.docx"
    }.Select(n => new object[] { n });

    public static bool IsPdf(byte[]? data) =>
        data is { Length: > 4 } && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F';

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wcpdf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
