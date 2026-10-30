using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WordCraftPdf.Internal;

namespace WordCraftPdf;

/// <summary>
/// Converts documents (DOCX, DOC, ODT, RTF, HTML, Markdown, plain text) to PDF with the
/// WordCraft engine.
///
/// <para><b>Process isolation:</b> conversions run in a pool of persistent <c>wcpdf_worker</c>
/// processes that the converter talks to over pipes; no native code is loaded into the .NET
/// process. If the engine crashes on a document, only its worker dies: the conversion returns a
/// failed result with <see cref="WordCraftPdfErrorCode.WorkerCrashed"/> and the next conversion
/// runs on a fresh worker. A conversion that exceeds
/// <see cref="PdfConverterOptions.ConversionTimeout"/> returns a failed result with
/// <see cref="WordCraftPdfErrorCode.Timeout"/>. Cancelling a conversion's token kills its worker
/// (the protocol can't interrupt a conversion) and throws <see cref="OperationCanceledException"/>.</para>
///
/// <para><b>Thread safety and performance:</b> all methods are thread-safe. Up to
/// <see cref="PdfConverterOptions.MaxWorkers"/> conversions run in parallel; each one goes to
/// an idle worker, and workers are started only when all running ones are busy.</para>
///
/// <para><b>Conversion modes:</b>
/// <list type="bullet">
/// <item><b>File mode</b> — <see cref="ConvertAsync(string, string, ConversionOptions?, CancellationToken)"/>
/// sends only paths: the worker reads the document and writes the PDF itself (atomically).
/// The .NET process never holds the bytes. Best for batch processing and large files.</item>
/// <item><b>Buffer mode</b> — every other overload sends the document over the pipe and
/// receives the PDF the same way; no temporary files are created.</item>
/// </list></para>
///
/// <para><b>Errors:</b> conversion failures are reported in the returned
/// <see cref="ConversionResult"/> (check <see cref="ConversionResult.Success"/> or call
/// <see cref="ConversionResult.ThrowIfFailed"/>); exceptions are reserved for invalid arguments,
/// cancellation and use after dispose.</para>
///
/// <example>
/// <code>
/// await using var converter = PdfConverter.Create(new PdfConverterOptions
/// {
///     FontDirectories = new[] { "/usr/share/fonts/custom" },
///     MaxWorkers = 4
/// });
///
/// // File to file
/// var result = await converter.ConvertAsync("input.docx", "output.pdf");
///
/// // Bytes to bytes (format sniffed from the content)
/// var pdf = await converter.ConvertAsync(docxBytes);
/// byte[] bytes = pdf.ThrowIfFailed().Data!;
///
/// // Stream to stream (e.g. an ASP.NET controller)
/// await converter.ConvertAsync(Request.Body, Response.Body, DocumentFormat.Docx);
/// </code>
/// </example>
/// </summary>
public sealed class PdfConverter : IAsyncDisposable, IDisposable
{
    private readonly WorkerPool _pool;
    private volatile bool _disposed;

    private PdfConverter(WorkerPool pool)
    {
        _pool = pool;
    }

    /// <summary>
    /// Create a converter. Workers start on the first conversion unless
    /// <see cref="PdfConverterOptions.WarmUp"/> is true.
    /// </summary>
    /// <param name="options">Converter configuration. Null for defaults.</param>
    /// <returns>A new converter. Dispose it to stop its workers.</returns>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    /// <exception cref="FileNotFoundException">The worker executable can't be found.</exception>
    /// <exception cref="WordCraftPdfException">With <see cref="PdfConverterOptions.WarmUp"/>, a worker failed to start.</exception>
    public static PdfConverter Create(PdfConverterOptions? options = null)
    {
        var converter = CreateCore(options ?? new PdfConverterOptions());
        if (options?.WarmUp == true)
        {
            try
            {
                // Create is synchronous by contract; CreateAsync is the non-blocking alternative.
                converter._pool.WarmUpAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                converter.Dispose();
                throw;
            }
        }
        return converter;
    }

    /// <summary>
    /// Create a converter without blocking; with <see cref="PdfConverterOptions.WarmUp"/> the
    /// returned task completes once every worker is ready.
    /// </summary>
    /// <inheritdoc cref="Create" />
    public static async Task<PdfConverter> CreateAsync(
        PdfConverterOptions? options = null, CancellationToken cancellationToken = default)
    {
        var converter = CreateCore(options ?? new PdfConverterOptions());
        if (options?.WarmUp == true)
        {
            try
            {
                await converter._pool.WarmUpAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await converter.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        return converter;
    }

    private static PdfConverter CreateCore(PdfConverterOptions options)
    {
        if (options.MaxWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxWorkers must be at least 1.");
        if (options.MaxConversionsPerWorker < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConversionsPerWorker must be 0 (never recycle) or positive.");
        ValidateTimeout(options.ConversionTimeout, nameof(PdfConverterOptions.ConversionTimeout));
        ValidateTimeout(options.WorkerStartupTimeout, nameof(PdfConverterOptions.WorkerStartupTimeout));
        if (options.ShutdownTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "ShutdownTimeout must not be negative.");

        string[]? fontPaths = null;
        if (options.FontDirectories is { Count: > 0 } dirs)
        {
            fontPaths = new string[dirs.Count];
            for (int i = 0; i < dirs.Count; i++)
            {
                if (string.IsNullOrEmpty(dirs[i]))
                    throw new ArgumentException("FontDirectories must not contain null or empty entries.", nameof(options));
                fontPaths[i] = Path.GetFullPath(dirs[i]);
            }
        }

        var workerPath = WorkerLocator.FindWorkerExecutable(options.WorkerPath);
        var pool = new WorkerPool(
            workerPath,
            fontPaths,
            options.MaxWorkers,
            options.MaxConversionsPerWorker,
            options.ConversionTimeout,
            options.WorkerStartupTimeout,
            options.ShutdownTimeout);
        return new PdfConverter(pool);
    }

    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value == Timeout.InfiniteTimeSpan)
            return;
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(
                name, $"{name} must be positive and at most {int.MaxValue} ms, or Timeout.InfiniteTimeSpan.");
    }

    /// <summary>
    /// Version of the WordCraftPdf worker and engine (e.g. "WordCraftPdf 0.1.0 (WordCraft v0.12.0)").
    /// Null until a worker has started.
    /// </summary>
    public string? Version => _pool.Version;

    /// <summary>
    /// Diagnostics reported when a worker started: font folders or files from
    /// <see cref="PdfConverterOptions.FontDirectories"/> that couldn't be used. Empty until a
    /// worker has started.
    /// </summary>
    public IReadOnlyList<ConversionDiagnostic> StartupDiagnostics => _pool.StartupDiagnostics;

    /// <summary>
    /// Start all <see cref="PdfConverterOptions.MaxWorkers"/> workers now, so the first
    /// conversions don't pay the start-up cost.
    /// </summary>
    /// <exception cref="WordCraftPdfException">A worker failed to start.</exception>
    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        ThrowHelpers.ThrowIfDisposed(_disposed, this);
        return _pool.WarmUpAsync(cancellationToken);
    }

    /// <summary>
    /// Convert a document file to a PDF file (file mode).
    /// </summary>
    /// <param name="inputPath">Input document. The format is detected from the extension
    /// (unknown extensions are left to the worker).</param>
    /// <param name="outputPath">Output PDF path. Written atomically by the worker.</param>
    /// <param name="options">Conversion options. Null for defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion (kills its worker, then throws
    /// <see cref="OperationCanceledException"/>).</param>
    /// <returns>The result with diagnostics and page count. Check <see cref="ConversionResult.Success"/>.</returns>
    /// <remarks>Only the paths are sent to the worker: the .NET process never holds the bytes.</remarks>
    public async Task<ConversionResult> ConvertAsync(
        string inputPath,
        string outputPath,
        ConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowHelpers.ThrowIfDisposed(_disposed, this);
        ThrowHelpers.ThrowIfNullOrEmpty(inputPath);
        ThrowHelpers.ThrowIfNullOrEmpty(outputPath);

        inputPath = Path.GetFullPath(inputPath);
        outputPath = Path.GetFullPath(outputPath);

        if (!File.Exists(inputPath))
            return ConversionResult.Fail(
                $"Input file not found: {inputPath}", WordCraftPdfErrorCode.FileNotFound, null);

        return await _pool.ExecuteFileAsync(
                inputPath, outputPath, DetectFormat(inputPath), options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Convert an in-memory document to PDF bytes (buffer mode).
    /// </summary>
    /// <param name="input">Document bytes. Sent to the worker without copying.</param>
    /// <param name="format">Document format. <see cref="DocumentFormat.Unknown"/> (default)
    /// detects DOCX, DOC, ODT and RTF from the content; HTML, Markdown and text need an
    /// explicit format.</param>
    /// <param name="options">Conversion options. Null for defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion (kills its worker, then throws
    /// <see cref="OperationCanceledException"/>).</param>
    /// <returns>The result; <see cref="ConversionResult{T}.Data"/> holds the PDF on success.</returns>
    /// <remarks>
    /// <paramref name="input"/> must not be modified until the returned task completes.
    /// </remarks>
    public Task<ConversionResult<byte[]>> ConvertAsync(
        ReadOnlyMemory<byte> input,
        DocumentFormat format = DocumentFormat.Unknown,
        ConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowHelpers.ThrowIfDisposed(_disposed, this);
        if (input.IsEmpty)
            return Task.FromResult(ConversionResult<byte[]>.Fail(
                "Input data is empty", WordCraftPdfErrorCode.InvalidArgument, null));
        if (input.Length > Protocol.MaxFrameSize)
            return Task.FromResult(ConversionResult<byte[]>.Fail(
                TooLargeMessage(input.Length), WordCraftPdfErrorCode.InvalidArgument, null));
        if (!IsValidFormat(format))
            return Task.FromResult(ConversionResult<byte[]>.Fail(
                InvalidFormatMessage(format), WordCraftPdfErrorCode.InvalidFormat, null));

        return _pool.ExecuteBufferAsync(input, format, options, cancellationToken);
    }

    /// <summary>
    /// Convert a document stream to PDF, writing the PDF to an output stream (buffer mode).
    /// </summary>
    /// <param name="input">Readable stream with the document; read from its current position to its end.</param>
    /// <param name="output">Writable stream that receives the PDF (only on success).</param>
    /// <param name="format">Document format; <see cref="DocumentFormat.Unknown"/> sniffs the content.</param>
    /// <param name="options">Conversion options. Null for defaults.</param>
    /// <param name="cancellationToken">Cancels reading, the conversion and writing.</param>
    /// <returns>The result with diagnostics and page count.</returns>
    /// <remarks>
    /// A <see cref="MemoryStream"/> with an accessible buffer is sent without copying; another
    /// seekable stream is read once into an exactly-sized array; a non-seekable stream is
    /// read into pooled buffers. No temporary files are created.
    /// </remarks>
    public async Task<ConversionResult> ConvertAsync(
        Stream input,
        Stream output,
        DocumentFormat format = DocumentFormat.Unknown,
        ConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowHelpers.ThrowIfDisposed(_disposed, this);
        ThrowHelpers.ThrowIfNull(input);
        ThrowHelpers.ThrowIfNull(output);

        if (!input.CanRead)
            return ConversionResult.Fail("Input stream is not readable", WordCraftPdfErrorCode.InvalidArgument, null);
        if (!output.CanWrite)
            return ConversionResult.Fail("Output stream is not writable", WordCraftPdfErrorCode.InvalidArgument, null);

        var result = await ConvertStreamCoreAsync(input, format, options, cancellationToken).ConfigureAwait(false);
        if (result.Success && result.Data is { } pdf)
        {
#if NET8_0_OR_GREATER
            await output.WriteAsync(pdf, cancellationToken).ConfigureAwait(false);
#else
            await output.WriteAsync(pdf, 0, pdf.Length, cancellationToken).ConfigureAwait(false);
#endif
        }
        return result.AsBase();
    }

    /// <summary>
    /// Convert a document stream to a PDF file (buffer mode).
    /// </summary>
    /// <param name="input">Readable stream with the document; read from its current position to its end.</param>
    /// <param name="outputPath">Output PDF path (written only on success).</param>
    /// <param name="format">Document format; <see cref="DocumentFormat.Unknown"/> sniffs the content.</param>
    /// <param name="options">Conversion options. Null for defaults.</param>
    /// <param name="cancellationToken">Cancels reading, the conversion and writing.</param>
    /// <returns>The result with diagnostics and page count.</returns>
    public async Task<ConversionResult> ConvertAsync(
        Stream input,
        string outputPath,
        DocumentFormat format = DocumentFormat.Unknown,
        ConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowHelpers.ThrowIfDisposed(_disposed, this);
        ThrowHelpers.ThrowIfNull(input);
        ThrowHelpers.ThrowIfNullOrEmpty(outputPath);

        if (!input.CanRead)
            return ConversionResult.Fail("Input stream is not readable", WordCraftPdfErrorCode.InvalidArgument, null);

        var result = await ConvertStreamCoreAsync(input, format, options, cancellationToken).ConfigureAwait(false);
        if (result.Success && result.Data is { } pdf)
            await WriteFileAsync(Path.GetFullPath(outputPath), pdf, cancellationToken).ConfigureAwait(false);
        return result.AsBase();
    }

    /// <summary>
    /// Convert a document file to PDF, writing the PDF to an output stream (buffer mode).
    /// </summary>
    /// <param name="inputPath">Input document. The format is detected from the extension, or
    /// from the content when the extension is unknown.</param>
    /// <param name="output">Writable stream that receives the PDF (only on success).</param>
    /// <param name="options">Conversion options. Null for defaults.</param>
    /// <param name="cancellationToken">Cancels reading, the conversion and writing.</param>
    /// <returns>The result with diagnostics and page count.</returns>
    /// <remarks>The file is read once into an exactly-sized array and sent over the pipe.</remarks>
    public async Task<ConversionResult> ConvertAsync(
        string inputPath,
        Stream output,
        ConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowHelpers.ThrowIfDisposed(_disposed, this);
        ThrowHelpers.ThrowIfNullOrEmpty(inputPath);
        ThrowHelpers.ThrowIfNull(output);

        if (!output.CanWrite)
            return ConversionResult.Fail("Output stream is not writable", WordCraftPdfErrorCode.InvalidArgument, null);

        inputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(inputPath))
            return ConversionResult.Fail(
                $"Input file not found: {inputPath}", WordCraftPdfErrorCode.FileNotFound, null);

        ConversionResult<byte[]> result;
        using (var file = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                   bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            result = await ConvertStreamCoreAsync(file, DetectFormat(inputPath), options, cancellationToken)
                .ConfigureAwait(false);
        }

        if (result.Success && result.Data is { } pdf)
        {
#if NET8_0_OR_GREATER
            await output.WriteAsync(pdf, cancellationToken).ConfigureAwait(false);
#else
            await output.WriteAsync(pdf, 0, pdf.Length, cancellationToken).ConfigureAwait(false);
#endif
        }
        return result.AsBase();
    }

    /// <summary>
    /// Dispose the converter: idle workers are asked to quit; running conversions get a few
    /// seconds to finish, then their workers are killed (those conversions fail with
    /// <see cref="WordCraftPdfErrorCode.WorkerCrashed"/>).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _pool.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Synchronous dispose (blocks until the workers have exited).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Dispose is synchronous by contract: blocking here is inherent.
        _pool.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    internal WorkerPool Pool => _pool;

    // ------------------------------------------------------------------ helpers

    /// <summary>Format from a file extension (case-insensitive); Unknown when unrecognised.</summary>
    internal static DocumentFormat DetectFormat(string path) =>
        Path.GetExtension(path)?.ToLowerInvariant() switch
        {
            ".docx" or ".docm" or ".dotx" or ".dotm" => DocumentFormat.Docx,
            ".doc" or ".dot" => DocumentFormat.Doc,
            ".odt" => DocumentFormat.Odt,
            ".rtf" => DocumentFormat.Rtf,
            ".html" or ".htm" => DocumentFormat.Html,
            ".md" or ".markdown" => DocumentFormat.Markdown,
            ".txt" => DocumentFormat.Text,
            _ => DocumentFormat.Unknown
        };

    internal static bool IsValidFormat(DocumentFormat format) =>
        format >= DocumentFormat.Unknown && format <= DocumentFormat.Text;

    private static string InvalidFormatMessage(DocumentFormat format) =>
        $"Unsupported document format value {(int)format}.";

    private static string TooLargeMessage(long size) =>
        $"Input is {size} bytes; the largest document that can be sent to the worker is {Protocol.MaxFrameSize} bytes.";

    private async Task<ConversionResult<byte[]>> ConvertStreamCoreAsync(
        Stream input, DocumentFormat format, ConversionOptions? options, CancellationToken ct)
    {
        if (!IsValidFormat(format))
            return ConversionResult<byte[]>.Fail(InvalidFormatMessage(format), WordCraftPdfErrorCode.InvalidFormat, null);

        var buffer = await ReadInputAsync(input, ct).ConfigureAwait(false);
        try
        {
            if (buffer.TooLarge)
                return ConversionResult<byte[]>.Fail(
                    TooLargeMessage(buffer.Length), WordCraftPdfErrorCode.InvalidArgument, null);
            if (buffer.Memory.IsEmpty)
                return ConversionResult<byte[]>.Fail("Input data is empty", WordCraftPdfErrorCode.InvalidArgument, null);
            return await _pool.ExecuteBufferAsync(buffer.Memory, format, options, ct).ConfigureAwait(false);
        }
        finally
        {
            // The conversion has finished with the bytes (written or abandoned) by now.
            buffer.Release();
        }
    }

    /// <summary>Input bytes, possibly in a pooled array that must be released after use.</summary>
    private readonly struct InputBuffer
    {
        public InputBuffer(ReadOnlyMemory<byte> memory, byte[]? pooled, long length, bool tooLarge)
        {
            Memory = memory;
            Pooled = pooled;
            Length = length;
            TooLarge = tooLarge;
        }

        public ReadOnlyMemory<byte> Memory { get; }
        public byte[]? Pooled { get; }
        public long Length { get; }
        public bool TooLarge { get; }

        public void Release()
        {
            if (Pooled is not null)
                ArrayPool<byte>.Shared.Return(Pooled);
        }
    }

    private static async Task<InputBuffer> ReadInputAsync(Stream stream, CancellationToken ct)
    {
        // A MemoryStream with an exposed buffer: no copy at all.
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var segment))
        {
            var position = (int)ms.Position;
            var length = (int)ms.Length - position;
            ms.Position = ms.Length;
            return length > Protocol.MaxFrameSize
                ? new InputBuffer(default, null, length, tooLarge: true)
                : new InputBuffer(new ReadOnlyMemory<byte>(segment.Array!, segment.Offset + position, Math.Max(length, 0)), null, length, false);
        }

        // Seekable with a known length: one exactly-sized array, filled in one pass.
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining > Protocol.MaxFrameSize)
                return new InputBuffer(default, null, remaining, tooLarge: true);
            if (remaining <= 0)
                return new InputBuffer(default, null, 0, false);
            var data = new byte[remaining];
            int read = await Protocol.ReadExactAsync(stream, data, 0, data.Length, ct).ConfigureAwait(false);
            if (read == data.Length)
                return new InputBuffer(data, null, read, false);
            return new InputBuffer(new ReadOnlyMemory<byte>(data, 0, read), null, read, false); // stream was shorter than announced
        }

        // Unknown length: grow a pooled buffer.
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        int filled = 0;
        try
        {
            while (true)
            {
                if (filled > Protocol.MaxFrameSize)
                    break;
                if (filled == buffer.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)buffer.Length * 2, Protocol.MaxFrameSize + 1L));
                    Buffer.BlockCopy(buffer, 0, bigger, 0, filled);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
#if NET8_0_OR_GREATER
                int n = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
#else
                int n = await stream.ReadAsync(buffer, filled, buffer.Length - filled, ct).ConfigureAwait(false);
#endif
                if (n == 0)
                    break;
                filled += n;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        if (filled > Protocol.MaxFrameSize)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return new InputBuffer(default, null, filled, tooLarge: true);
        }
        return new InputBuffer(new ReadOnlyMemory<byte>(buffer, 0, filled), buffer, filled, false);
    }

    private static async Task WriteFileAsync(string path, byte[] data, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.Asynchronous);
#if NET8_0_OR_GREATER
        await file.WriteAsync(data, ct).ConfigureAwait(false);
#else
        await file.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
#endif
    }
}
