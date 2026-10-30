using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WordCraftPdf.Internal;

/// <summary>
/// Locates the <c>wcpdf_worker</c> executable. Search order:
/// <list type="number">
/// <item><see cref="PdfConverterOptions.WorkerPath"/></item>
/// <item>the <c>WCPDF_WORKER_PATH</c> environment variable</item>
/// <item><c>AppContext.BaseDirectory/runtimes/&lt;rid&gt;/native/</c> (NuGet layout, portable app)</item>
/// <item><c>AppContext.BaseDirectory/</c> (NuGet layout flattened by a RID-specific build)</item>
/// <item><c>output/bin/</c> in AppContext.BaseDirectory or one of its parents (development checkout)</item>
/// </list>
/// An explicit path (option or environment variable) that does not exist is an error rather
/// than a reason to keep searching, so a misconfiguration is never silently ignored.
/// </summary>
internal static class WorkerLocator
{
    public const string EnvironmentVariable = "WCPDF_WORKER_PATH";

    private const int MaxParentLevels = 10;

    public static string ExecutableName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "wcpdf_worker.exe" : "wcpdf_worker";

    /// <summary>Find the worker, make sure it is executable, and return its full path.</summary>
    /// <exception cref="FileNotFoundException">No worker found.</exception>
    public static string FindWorkerExecutable(string? explicitPath = null) =>
        FindWorkerExecutable(explicitPath, Environment.GetEnvironmentVariable(EnvironmentVariable), AppContext.BaseDirectory);

    internal static string FindWorkerExecutable(string? explicitPath, string? environmentPath, string baseDirectory)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return Accept(explicitPath!, $"{nameof(PdfConverterOptions)}.{nameof(PdfConverterOptions.WorkerPath)}");

        if (!string.IsNullOrEmpty(environmentPath))
            return Accept(environmentPath!, $"the {EnvironmentVariable} environment variable");

        var searched = new List<string>();
        foreach (var candidate in GetCandidates(baseDirectory))
        {
            searched.Add(candidate);
            if (File.Exists(candidate))
                return Prepare(Path.GetFullPath(candidate));
        }

        throw new FileNotFoundException(
            $"Cannot find the {ExecutableName} executable. Install the WordCraftPdf.NativeAssets package " +
            $"for this platform ({GetPortableRuntimeIdentifier()}), set {nameof(PdfConverterOptions)}." +
            $"{nameof(PdfConverterOptions.WorkerPath)}, or set the {EnvironmentVariable} environment variable. " +
            $"Searched: {string.Join(", ", searched)}",
            ExecutableName);
    }

    /// <summary>The automatic search locations, in order.</summary>
    internal static IEnumerable<string> GetCandidates(string baseDirectory)
    {
        var name = ExecutableName;
        yield return Path.Combine(baseDirectory, "runtimes", GetPortableRuntimeIdentifier(), "native", name);
        yield return Path.Combine(baseDirectory, name);

        // Development fallback: a checkout where scripts/build.sh wrote output/bin/.
        var dir = new DirectoryInfo(baseDirectory);
        for (int i = 0; i < MaxParentLevels && dir != null; i++, dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "output", "bin", name);
    }

    private static string Accept(string path, string source)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException(
                $"The {ExecutableName} executable configured by {source} does not exist: {full}", full);
        return Prepare(full);
    }

    /// <summary>
    /// Portable runtime identifier of the current process: linux-x64, linux-arm64, osx-arm64,
    /// osx-x64, win-x64, win-arm64, … (the NuGet <c>runtimes/</c> folder names).
    /// </summary>
    public static string GetPortableRuntimeIdentifier()
    {
        string os;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            os = "win";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            os = "osx";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            os = "linux";
        else
            os = "unix";

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant()
        };
        return $"{os}-{arch}";
    }

    /// <summary>
    /// On Unix, make sure the worker has its execute bits: NuGet does not preserve file modes
    /// when it extracts packages (the NativeAssets .targets fix the copy in the build output,
    /// this covers the cases they can't, e.g. running from the package cache).
    /// </summary>
    internal static string Prepare(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            EnsureExecutable(path);
        return path;
    }

    internal static void EnsureExecutable(string path)
    {
        try
        {
#if NET7_0_OR_GREATER
            if (OperatingSystem.IsWindows())
                return;
            const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            var mode = File.GetUnixFileMode(path);
            if ((mode & UnixFileMode.UserExecute) == 0)
                File.SetUnixFileMode(path, mode | Execute);
#else
            // netstandard2.0 has no API to read the file mode: ask chmod (a no-op when the bits
            // are already set). Done once per converter, never per conversion.
            using var chmod = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = "a+x \"" + path.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });
            chmod?.WaitForExit(5000);
#endif
        }
        catch
        {
            // Read-only file system or not the owner: starting the worker will report the problem.
        }
    }
}
