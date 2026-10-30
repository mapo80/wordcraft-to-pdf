using System;
using System.Collections.Generic;

namespace WordCraftPdf;

/// <summary>
/// Configuration for creating a <see cref="PdfConverter"/> instance.
/// </summary>
public sealed class PdfConverterOptions
{
    /// <summary>
    /// Path of the <c>wcpdf_worker</c> executable. Null = locate it automatically: the
    /// <c>WCPDF_WORKER_PATH</c> environment variable, then
    /// <c>runtimes/&lt;rid&gt;/native/</c> and the application directory (where the
    /// WordCraftPdf.NativeAssets packages put it), then <c>output/bin/</c> in a parent
    /// directory (development checkout).
    /// </summary>
    public string? WorkerPath { get; init; }

    /// <summary>
    /// Font folders (searched recursively) whose fonts are used ahead of the installed fonts.
    /// Folders that can't be used are reported in <see cref="PdfConverter.StartupDiagnostics"/>.
    /// Fonts are loaded when a worker starts; changes require a new <see cref="PdfConverter"/>.
    /// </summary>
    public IReadOnlyList<string>? FontDirectories { get; init; }

    /// <summary>
    /// Longest time a worker may spend on a single conversion (time spent waiting for a free
    /// worker is not counted). When it is exceeded the worker process is killed and the
    /// conversion returns a <b>failed result</b> with <see cref="WordCraftPdfErrorCode.Timeout"/>
    /// (no exception is thrown); the next conversion runs on a fresh worker.
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables the limit. Default: 5 minutes.
    /// </summary>
    public TimeSpan ConversionTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Longest time a worker may take to start and initialise (load fonts, scan the system
    /// fonts and run its warm-up conversion). Default: 1 minute.
    /// </summary>
    public TimeSpan WorkerStartupTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum number of worker processes, i.e. of conversions running in parallel. Workers are
    /// started lazily, only when every running worker is busy. Default:
    /// <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public int MaxWorkers { get; init; } = Environment.ProcessorCount;

    /// <summary>
    /// Replace a worker process after this many conversions, to bound memory growth.
    /// 0 = never. Default: 0.
    /// </summary>
    public int MaxConversionsPerWorker { get; init; }

    /// <summary>
    /// If true, <see cref="PdfConverter.Create"/> starts all <see cref="MaxWorkers"/> workers
    /// before returning (and throws <see cref="WordCraftPdfException"/> if they can't start).
    /// If false (default), workers start on demand.
    /// </summary>
    public bool WarmUp { get; init; }

    /// <summary>
    /// How long disposing the converter waits for workers to finish their conversion and exit
    /// before killing them. Internal: tests shorten it.
    /// </summary>
    internal TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
