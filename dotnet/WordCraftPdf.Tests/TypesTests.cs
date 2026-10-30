using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using WordCraftPdf.Internal;
using Xunit;

namespace WordCraftPdf.Tests;

public class ConversionResultTests
{
    [Fact]
    public void Ok_ReturnsSuccess()
    {
        var result = ConversionResult.Ok(null, 3);
        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.ErrorCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.PageCount);
    }

    [Fact]
    public void Fail_ReturnsFailure()
    {
        var result = ConversionResult.Fail("boom", WordCraftPdfErrorCode.ExportFailed, null);
        Assert.False(result.Success);
        Assert.Equal("boom", result.ErrorMessage);
        Assert.Equal(WordCraftPdfErrorCode.ExportFailed, result.ErrorCode);
        Assert.Equal(0, result.PageCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ImplicitBool()
    {
        Assert.True(ConversionResult.Ok(null, 1));
        Assert.False(ConversionResult.Fail("x", WordCraftPdfErrorCode.Unknown, null));
    }

    [Fact]
    public void ThrowIfFailed_Throws_WithCode()
    {
        var ex = Assert.Throws<WordCraftPdfException>(() =>
            ConversionResult.Fail("load failed", WordCraftPdfErrorCode.LoadFailed, null).ThrowIfFailed());
        Assert.Equal(WordCraftPdfErrorCode.LoadFailed, ex.ErrorCode);
        Assert.Equal("load failed", ex.Message);
    }

    [Fact]
    public void ThrowIfFailed_ReturnsThis_OnSuccess()
    {
        var result = ConversionResult.Ok(null, 1);
        Assert.Same(result, result.ThrowIfFailed());
    }

    [Fact]
    public void Generic_ThrowIfFailed_ReturnsTypedResult()
    {
        var result = ConversionResult<byte[]>.Ok(new byte[] { 1 }, null, 1);
        byte[]? data = result.ThrowIfFailed().Data;
        Assert.Equal(new byte[] { 1 }, data);
        Assert.Throws<WordCraftPdfException>(() =>
            ConversionResult<byte[]>.Fail("x", WordCraftPdfErrorCode.Timeout, null).ThrowIfFailed());
    }

    [Fact]
    public void HasFontWarnings()
    {
        var font = new ConversionDiagnostic(DiagnosticSeverity.Warning, DiagnosticCategory.Font, "f", "Calibri", "Carlito");
        var general = new ConversionDiagnostic(DiagnosticSeverity.Info, DiagnosticCategory.General, "g");
        Assert.True(ConversionResult.Ok(new[] { general, font }, 1).HasFontWarnings);
        Assert.False(ConversionResult.Ok(new[] { general }, 1).HasFontWarnings);
        Assert.False(ConversionResult.Ok(null, 1).HasFontWarnings);
    }

    [Fact]
    public void AsBase_PreservesEverythingButData()
    {
        var diags = new[] { new ConversionDiagnostic(DiagnosticSeverity.Warning, DiagnosticCategory.Font, "f") };
        var ok = ConversionResult<byte[]>.Ok(new byte[] { 1, 2 }, diags, 7).AsBase();
        Assert.True(ok.Success);
        Assert.Equal(7, ok.PageCount);
        Assert.Same(diags, ok.Diagnostics);

        var failed = ConversionResult<byte[]>.Fail("bad", WordCraftPdfErrorCode.WorkerCrashed, diags).AsBase();
        Assert.False(failed.Success);
        Assert.Equal("bad", failed.ErrorMessage);
        Assert.Equal(WordCraftPdfErrorCode.WorkerCrashed, failed.ErrorCode);
        Assert.Same(diags, failed.Diagnostics);
    }

    [Fact]
    public void Generic_Fail_HasNullData()
    {
        Assert.Null(ConversionResult<byte[]>.Fail("x", WordCraftPdfErrorCode.Unknown, null).Data);
    }
}

public class ConversionDiagnosticTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var d = new ConversionDiagnostic(DiagnosticSeverity.Warning, DiagnosticCategory.Font, "msg", "Arial", "Liberation Sans");
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(DiagnosticCategory.Font, d.Category);
        Assert.Equal("msg", d.Message);
        Assert.Equal("Arial", d.FontName);
        Assert.Equal("Liberation Sans", d.SubstitutedWith);
        Assert.Equal("[Warning:Font] msg", d.ToString());
    }

    [Fact]
    public void OptionalProperties_DefaultToNull()
    {
        var d = new ConversionDiagnostic(DiagnosticSeverity.Info, DiagnosticCategory.General, "m");
        Assert.Null(d.FontName);
        Assert.Null(d.SubstitutedWith);
        Assert.Equal("[Info:General] m", d.ToString());
    }
}

public class OptionsTests
{
    [Fact]
    public void ConversionOptions_DefaultsAreOff()
    {
        var o = new ConversionOptions();
        Assert.Null(o.PageRange);
        Assert.Null(o.Password);
        Assert.False(o.TaggedPdf);
        Assert.False(o.UpdateFields);
        Assert.False(o.IncludeMarkup);
        Assert.False(o.IncludeComments);
        Assert.Null(o.Title);
        Assert.Null(o.Author);
    }

    [Fact]
    public void PdfConverterOptions_Defaults()
    {
        var o = new PdfConverterOptions();
        Assert.Null(o.WorkerPath);
        Assert.Null(o.FontDirectories);
        Assert.Equal(TimeSpan.FromMinutes(5), o.ConversionTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), o.WorkerStartupTimeout);
        Assert.Equal(Environment.ProcessorCount, o.MaxWorkers);
        Assert.Equal(0, o.MaxConversionsPerWorker);
        Assert.False(o.WarmUp);
        Assert.Equal(TimeSpan.FromSeconds(5), o.ShutdownTimeout);
    }
}

public class WordCraftPdfExceptionTests
{
    [Fact]
    public void Constructors()
    {
        Assert.Equal(WordCraftPdfErrorCode.Unknown, new WordCraftPdfException("m").ErrorCode);
        var inner = new IOException("io");
        var ex = new WordCraftPdfException("m", WordCraftPdfErrorCode.InitFailed, inner);
        Assert.Equal(WordCraftPdfErrorCode.InitFailed, ex.ErrorCode);
        Assert.Same(inner, ex.InnerException);
        Assert.IsAssignableFrom<Exception>(ex);
    }
}

public class EnumTests
{
    [Theory]
    [InlineData(DocumentFormat.Unknown, 0)]
    [InlineData(DocumentFormat.Docx, 1)]
    [InlineData(DocumentFormat.Doc, 2)]
    [InlineData(DocumentFormat.Odt, 3)]
    [InlineData(DocumentFormat.Rtf, 4)]
    [InlineData(DocumentFormat.Html, 5)]
    [InlineData(DocumentFormat.Markdown, 6)]
    [InlineData(DocumentFormat.Text, 7)]
    public void DocumentFormat_MatchesProtocol(DocumentFormat format, int expected) => Assert.Equal(expected, (int)format);

    [Theory]
    [InlineData(WordCraftPdfErrorCode.InitFailed, 1)]
    [InlineData(WordCraftPdfErrorCode.LoadFailed, 2)]
    [InlineData(WordCraftPdfErrorCode.ExportFailed, 3)]
    [InlineData(WordCraftPdfErrorCode.InvalidFormat, 4)]
    [InlineData(WordCraftPdfErrorCode.FileNotFound, 5)]
    [InlineData(WordCraftPdfErrorCode.OutOfMemory, 6)]
    [InlineData(WordCraftPdfErrorCode.PermissionDenied, 7)]
    [InlineData(WordCraftPdfErrorCode.NotInitialized, 9)]
    [InlineData(WordCraftPdfErrorCode.InvalidArgument, 10)]
    [InlineData(WordCraftPdfErrorCode.PasswordRequired, 11)]
    [InlineData(WordCraftPdfErrorCode.WrongPassword, 12)]
    [InlineData(WordCraftPdfErrorCode.Unknown, 99)]
    [InlineData(WordCraftPdfErrorCode.WorkerCrashed, 100)]
    [InlineData(WordCraftPdfErrorCode.Timeout, 101)]
    public void ErrorCode_MatchesProtocol(WordCraftPdfErrorCode code, int expected) => Assert.Equal(expected, (int)code);

    [Fact]
    public void ErrorCode_HasNoOtherValues() => Assert.Equal(14, Enum.GetValues(typeof(WordCraftPdfErrorCode)).Length);
}

public class FormatDetectionTests
{
    [Theory]
    [InlineData("a.docx", DocumentFormat.Docx)]
    [InlineData("A.DOCX", DocumentFormat.Docx)]
    [InlineData("a.docm", DocumentFormat.Docx)]
    [InlineData("a.dotx", DocumentFormat.Docx)]
    [InlineData("a.dotm", DocumentFormat.Docx)]
    [InlineData("a.doc", DocumentFormat.Doc)]
    [InlineData("a.dot", DocumentFormat.Doc)]
    [InlineData("a.odt", DocumentFormat.Odt)]
    [InlineData("a.rtf", DocumentFormat.Rtf)]
    [InlineData("a.html", DocumentFormat.Html)]
    [InlineData("a.htm", DocumentFormat.Html)]
    [InlineData("a.md", DocumentFormat.Markdown)]
    [InlineData("a.markdown", DocumentFormat.Markdown)]
    [InlineData("a.txt", DocumentFormat.Text)]
    [InlineData("a.xlsx", DocumentFormat.Unknown)]
    [InlineData("noextension", DocumentFormat.Unknown)]
    [InlineData("/dir.docx/file", DocumentFormat.Unknown)]
    public void DetectFormat_FromExtension(string path, DocumentFormat expected) =>
        Assert.Equal(expected, PdfConverter.DetectFormat(path));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void IsValidFormat(int value, bool expected) => Assert.Equal(expected, PdfConverter.IsValidFormat((DocumentFormat)value));
}

public class WorkerLocatorTests : IDisposable
{
    private readonly string _dir = TestHelpers.NewTempDir();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine(new[] { _dir }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private static string Name => WorkerLocator.ExecutableName;

    [Fact]
    public void ExplicitPath_WinsOverEverything()
    {
        var explicitPath = Touch("explicit", Name);
        var envPath = Touch("env", Name);
        Touch(Name);
        Assert.Equal(explicitPath, WorkerLocator.FindWorkerExecutable(explicitPath, envPath, _dir));
    }

    [Fact]
    public void ExplicitPath_Missing_Throws()
    {
        var envPath = Touch("env", Name);
        var ex = Assert.Throws<FileNotFoundException>(() =>
            WorkerLocator.FindWorkerExecutable(Path.Combine(_dir, "nope"), envPath, _dir));
        Assert.Contains("WorkerPath", ex.Message);
    }

    [Fact]
    public void EnvironmentPath_WinsOverSearch()
    {
        var envPath = Touch("env", Name);
        Touch(Name);
        Assert.Equal(envPath, WorkerLocator.FindWorkerExecutable(null, envPath, _dir));
    }

    [Fact]
    public void EnvironmentPath_Missing_Throws()
    {
        Touch(Name);
        var ex = Assert.Throws<FileNotFoundException>(() =>
            WorkerLocator.FindWorkerExecutable(null, Path.Combine(_dir, "nope"), _dir));
        Assert.Contains(WorkerLocator.EnvironmentVariable, ex.Message);
    }

    [Fact]
    public void RuntimesFolder_ComesBeforeBaseDirectory()
    {
        var rid = WorkerLocator.GetPortableRuntimeIdentifier();
        var nuget = Touch("runtimes", rid, "native", Name);
        Touch(Name);
        Assert.Equal(nuget, WorkerLocator.FindWorkerExecutable(null, null, _dir));
    }

    [Fact]
    public void BaseDirectory_IsSearched()
    {
        var flat = Touch(Name);
        Assert.Equal(flat, WorkerLocator.FindWorkerExecutable(null, null, _dir));
    }

    [Fact]
    public void DevelopmentFallback_WalksUpToOutputBin()
    {
        var dev = Touch("output", "bin", Name);
        var deep = Path.Combine(_dir, "dotnet", "Tests", "bin", "Debug", "net8.0");
        Directory.CreateDirectory(deep);
        Assert.Equal(dev, WorkerLocator.FindWorkerExecutable(null, null, deep));
    }

    [Fact]
    public void NotFound_ListsSearchedLocations()
    {
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        var ex = Assert.Throws<FileNotFoundException>(() => WorkerLocator.FindWorkerExecutable(null, null, empty));
        Assert.Contains(WorkerLocator.EnvironmentVariable, ex.Message);
        Assert.Contains(Path.Combine(empty, "runtimes"), ex.Message);
        Assert.DoesNotContain("http", ex.Message);
    }

    [Fact]
    public void Candidates_OrderMatchesDocumentation()
    {
        var c = WorkerLocator.GetCandidates(_dir).ToList();
        Assert.Equal(Path.Combine(_dir, "runtimes", WorkerLocator.GetPortableRuntimeIdentifier(), "native", Name), c[0]);
        Assert.Equal(Path.Combine(_dir, Name), c[1]);
        Assert.Equal(Path.Combine(_dir, "output", "bin", Name), c[2]);
    }

    [Fact]
    public void PortableRid_HasOsAndArch()
    {
        var rid = WorkerLocator.GetPortableRuntimeIdentifier();
        Assert.Matches("^(win|osx|linux|unix)-(x64|arm64|x86|arm|[a-z0-9]+)$", rid);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            Assert.Equal("osx-arm64", rid);
    }

    [Fact]
    public void ExecuteBit_IsRestoredOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = Touch("noexec", Name);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        WorkerLocator.FindWorkerExecutable(path, null, _dir);
        Assert.True((File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0);
    }
}
