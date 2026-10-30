using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WordCraftPdf.Internal;

/// <summary>
/// One <c>wcpdf_worker</c> process: start-up handshake, request/response exchange, bounded
/// stderr capture, and crash / timeout / cancellation handling.
///
/// <para>Native code never runs in the host: if the worker aborts, overflows its stack or is
/// killed, the host only sees end of stream on stdout. A worker that failed in any way is
/// marked faulted and never reused. The worker exits by itself when its stdin closes, so it
/// cannot outlive the host.</para>
///
/// <para>Not designed for concurrent conversions: the pool hands a worker to one caller at a
/// time. An internal semaphore only coordinates a conversion with <see cref="DisposeAsync"/>.</para>
/// </summary>
internal sealed class WorkerProcess
{
    /// <summary>Bytes of stderr kept for error messages.</summary>
    internal const int StderrTailBytes = 64 * 1024;

    /// <summary>Response JSON buffers up to this size are kept for reuse.</summary>
    private const int RetainedResponseBuffer = 64 * 1024;

    private static readonly TimeSpan ExitInfoWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StderrFlushWait = TimeSpan.FromMilliseconds(500);

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly StderrTail _stderr = new(StderrTailBytes);
    private readonly Task _stderrPump;
    private readonly byte[] _header = new byte[Protocol.HeaderSize];
    private readonly RequestWriter _writer = new();
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly TaskCompletionSource<bool> _disposed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[] _responseBuffer = new byte[4096];
    private volatile bool _faulted;
    private volatile bool _killedByDispose;
    private volatile bool _isBusy;
    private int _disposeState;
    private int _conversionCount;

    private WorkerProcess(Process process)
    {
        _process = process;
        ProcessId = process.Id;
        _stdin = process.StandardInput.BaseStream;
        _stdout = new BufferedStream(process.StandardOutput.BaseStream, 64 * 1024);
        _stderrPump = Task.Run(() => _stderr.DrainAsync(process.StandardError.BaseStream));
    }

    /// <summary>Operating system id of the worker process.</summary>
    public int ProcessId { get; }

    /// <summary>Version reported in the <c>ready</c> message.</summary>
    public string? Version { get; private set; }

    /// <summary>Diagnostics of the <c>ready</c> message (unusable font folders…).</summary>
    public IReadOnlyList<ConversionDiagnostic> StartupDiagnostics { get; private set; } =
        Array.Empty<ConversionDiagnostic>();

    /// <summary>Conversions this worker has answered (successful or not).</summary>
    public int ConversionCount => Volatile.Read(ref _conversionCount);

    /// <summary>True while a request is being exchanged.</summary>
    public bool IsBusy => _isBusy;

    /// <summary>The retained tail of the worker's stderr.</summary>
    public string StderrTail => _stderr.ToString();

    /// <summary>
    /// Whether the worker can take a request: running, not faulted, not disposed.
    /// Safe to call at any time, including after <see cref="DisposeAsync"/>.
    /// </summary>
    public bool IsAlive
    {
        get
        {
            if (_faulted || Volatile.Read(ref _disposeState) != 0)
                return false;
            try
            {
                return !_process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    // ------------------------------------------------------------------ start-up

    /// <summary>Start a worker and complete the <c>init</c> → <c>ready</c> handshake.</summary>
    /// <exception cref="WordCraftPdfException">The worker can't be started or initialised (InitFailed).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the worker was killed.</exception>
    public static async Task<WorkerProcess> StartAsync(
        string workerPath,
        IReadOnlyList<string>? fontPaths,
        TimeSpan startupTimeout,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new WordCraftPdfException(
                $"Failed to start the worker process '{workerPath}': {ex.Message}",
                WordCraftPdfErrorCode.InitFailed, ex);
        }
        if (process is null)
            throw new WordCraftPdfException(
                $"Failed to start the worker process '{workerPath}'", WordCraftPdfErrorCode.InitFailed);

        var worker = new WorkerProcess(process);
        try
        {
            await worker.InitializeAsync(fontPaths, startupTimeout, ct).ConfigureAwait(false);
            return worker;
        }
        catch
        {
            worker.Kill();
            await worker.DisposeAsync(TimeSpan.Zero).ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(IReadOnlyList<string>? fontPaths, TimeSpan timeout, CancellationToken ct)
    {
        WorkerResponse? response = null;
        Exception? error = null;
        using (var guard = new AbortGuard(this, timeout, ct))
        {
            try
            {
                await Protocol.WriteAsync(_stdin, _writer.BuildInit(fontPaths), CancellationToken.None)
                    .ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
                response = await ReadResponseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsCommunicationFailure(ex))
            {
                error = ex;
            }

            if (response is not null && guard.Disarm())
            {
                if (response.Type == "ready")
                {
                    Version = response.Version;
                    StartupDiagnostics = response.Diagnostics;
                    return;
                }
                var detail = response.Type == "error"
                    ? response.Message ?? "unknown error"
                    : $"unexpected response type '{response.Type}'";
                throw new WordCraftPdfException(
                    $"Worker initialization failed: {detail}", WordCraftPdfErrorCode.InitFailed);
            }

            Kill();
            guard.Dispose(); // waits for a running abort callback
            if (guard.Reason == AbortGuard.Canceled)
                throw new OperationCanceledException("Worker start-up was canceled.", ct);
            if (guard.Reason == AbortGuard.TimedOut)
                throw new WordCraftPdfException(
                    $"The worker did not initialise within {FormatDuration(timeout)}; it was killed. {await DescribeExitAsync().ConfigureAwait(false)}",
                    WordCraftPdfErrorCode.InitFailed);
        }

        var reason = error is WorkerProtocolException ? $"Worker protocol error during initialization: {error.Message}. " : "";
        throw new WordCraftPdfException(
            $"{reason}The worker process exited during initialization. {await DescribeExitAsync().ConfigureAwait(false)}",
            WordCraftPdfErrorCode.InitFailed);
    }

    // ------------------------------------------------------------------ conversions

    /// <summary>
    /// <c>convert</c>: the worker reads <paramref name="input"/> and writes <paramref name="output"/>.
    /// Returns null if the worker turned out to be dead before the request could be sent
    /// (the caller may retry on another worker).
    /// </summary>
    public async Task<ConversionResult?> ConvertFileAsync(
        long id, string input, string output, DocumentFormat format, ConversionOptions? options,
        TimeSpan timeout, CancellationToken ct)
    {
        var outcome = await RunAsync(
            id, "result", readPdf: false, timeout, ct,
            w => w._writer.BuildConvert(id, input, output, format, options),
            ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);

        if (outcome.NotSent)
            return null;
        if (outcome.Response is not { } r)
            return ConversionResult.Fail(outcome.FailureMessage!, outcome.FailureCode, null);
        return r.Success
            ? ConversionResult.Ok(r.Diagnostics, r.PageCount)
            : ConversionResult.Fail(r.ErrorMessage ?? "Conversion failed", Protocol.MapErrorCode(r.ErrorCode), r.Diagnostics);
    }

    /// <summary>
    /// <c>convert_buffer</c>: the document travels in a frame written straight from
    /// <paramref name="data"/>, and the PDF comes back in an exactly-sized array.
    /// Returns null if the worker turned out to be dead before the request could be sent.
    /// </summary>
    public async Task<ConversionResult<byte[]>?> ConvertBufferAsync(
        long id, DocumentFormat format, ReadOnlyMemory<byte> data, ConversionOptions? options,
        TimeSpan timeout, CancellationToken ct)
    {
        var outcome = await RunAsync(
            id, "buffer_result", readPdf: true, timeout, ct,
            w => w._writer.BuildConvertBuffer(id, format, data.Length, options),
            data).ConfigureAwait(false);

        if (outcome.NotSent)
            return null;
        if (outcome.Response is not { } r)
            return ConversionResult<byte[]>.Fail(outcome.FailureMessage!, outcome.FailureCode, null);
        return r.Success
            ? ConversionResult<byte[]>.Ok(outcome.Pdf!, r.Diagnostics, r.PageCount)
            : ConversionResult<byte[]>.Fail(r.ErrorMessage ?? "Conversion failed", Protocol.MapErrorCode(r.ErrorCode), r.Diagnostics);
    }

    private readonly struct Outcome
    {
        public WorkerResponse? Response { get; init; }
        public byte[]? Pdf { get; init; }
        public WordCraftPdfErrorCode FailureCode { get; init; }
        public string? FailureMessage { get; init; }
        public bool NotSent { get; init; }

        public static Outcome Fail(WordCraftPdfErrorCode code, string message) =>
            new() { FailureCode = code, FailureMessage = message };
    }

    private async Task<Outcome> RunAsync(
        long id,
        string expectedType,
        bool readPdf,
        TimeSpan timeout,
        CancellationToken ct,
        Func<WorkerProcess, ReadOnlyMemory<byte>> buildRequest,
        ReadOnlyMemory<byte> data)
    {
        ct.ThrowIfCancellationRequested();
        await _busy.WaitAsync().ConfigureAwait(false);
        _isBusy = true;
        try
        {
            if (Volatile.Read(ref _disposeState) != 0)
                throw new ObjectDisposedException(nameof(PdfConverter));
            if (!IsAlive)
                return new Outcome { NotSent = true };

            bool sent = false;
            Exception? error = null;
            using var guard = new AbortGuard(this, timeout, ct);
            try
            {
                // JSON frame (+ the document frame's length prefix, built in the same buffer):
                // one write; then the document bytes, straight from the caller's memory.
                await Protocol.WriteAsync(_stdin, buildRequest(this), CancellationToken.None).ConfigureAwait(false);
                sent = true;
                await Protocol.WriteAsync(_stdin, data, CancellationToken.None).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);

                var response = await ReadResponseAsync().ConfigureAwait(false);
                if (response is not null)
                {
                    Validate(response, id, expectedType);
                    byte[]? pdf = null;
                    if (readPdf && response.Success)
                    {
                        pdf = await ReadPdfAsync(response).ConfigureAwait(false);
                        if (pdf is null)
                            response = null; // died while sending the PDF
                    }

                    if (response is not null)
                    {
                        Interlocked.Increment(ref _conversionCount);
                        // If a timeout or cancellation fired after the answer was complete, the
                        // worker has been killed but the answer is still good.
                        if (!guard.Disarm())
                            _faulted = true;
                        return new Outcome { Response = response, Pdf = pdf };
                    }
                }
            }
            catch (Exception ex) when (IsCommunicationFailure(ex))
            {
                error = ex;
            }
            catch
            {
                // Anything else (e.g. out of memory for the PDF) leaves the stream out of step.
                Kill();
                throw;
            }

            // The worker died, was killed, or is out of step: it can't serve another request.
            Kill();
            guard.Dispose(); // waits for a running abort callback, so Reason is final
            switch (guard.Reason)
            {
                case AbortGuard.Canceled:
                    throw new OperationCanceledException(
                        "The conversion was canceled; its worker process was killed.", ct);
                case AbortGuard.TimedOut:
                    return Outcome.Fail(WordCraftPdfErrorCode.Timeout,
                        $"The conversion did not finish within {FormatDuration(timeout)}; its worker process was killed.");
            }
            if (_killedByDispose)
                return Outcome.Fail(WordCraftPdfErrorCode.WorkerCrashed,
                    "The converter was disposed while the conversion was running; its worker process was killed.");
            if (!sent && error is IOException)
                return new Outcome { NotSent = true };
            if (error is WorkerProtocolException)
                return Outcome.Fail(WordCraftPdfErrorCode.Unknown,
                    $"Worker protocol error: {error.Message}. The worker was replaced.");
            return Outcome.Fail(WordCraftPdfErrorCode.WorkerCrashed,
                $"The worker process crashed during the conversion; it will be replaced. {await DescribeExitAsync().ConfigureAwait(false)}");
        }
        finally
        {
            _isBusy = false;
            _busy.Release();
        }
    }

    private static void Validate(WorkerResponse response, long id, string expectedType)
    {
        if (response.Type == "error")
            throw new WorkerProtocolException($"worker rejected the request: {response.Message ?? "unknown error"}");
        if (response.Type != expectedType)
            throw new WorkerProtocolException($"expected '{expectedType}', worker sent '{response.Type}'");
        if (response.Id != id)
            throw new WorkerProtocolException($"expected an answer to request {id}, worker answered {response.Id?.ToString(CultureInfo.InvariantCulture) ?? "no id"}");
    }

    /// <summary>Read one JSON frame (reusing a buffer for typical sizes). Null at end of stream.</summary>
    private async Task<WorkerResponse?> ReadResponseAsync()
    {
        int length = await Protocol.ReadFrameLengthAsync(_stdout, _header, CancellationToken.None).ConfigureAwait(false);
        if (length < 0)
            return null;
        byte[] buffer;
        if (length <= RetainedResponseBuffer)
        {
            if (_responseBuffer.Length < length)
                _responseBuffer = new byte[Math.Min(RetainedResponseBuffer, Math.Max(length, _responseBuffer.Length * 2))];
            buffer = _responseBuffer;
        }
        else
        {
            buffer = new byte[length];
        }
        int read = await Protocol.ReadExactAsync(_stdout, buffer, 0, length, CancellationToken.None).ConfigureAwait(false);
        if (read < length)
            return null;
        return WorkerResponse.Parse(new ReadOnlyMemory<byte>(buffer, 0, length));
    }

    /// <summary>Read the PDF frame that follows a successful <c>buffer_result</c>. Null at end of stream.</summary>
    private async Task<byte[]?> ReadPdfAsync(WorkerResponse response)
    {
        int length = await Protocol.ReadFrameLengthAsync(_stdout, _header, CancellationToken.None).ConfigureAwait(false);
        if (length < 0)
            return null;
        if (response.DataSize is long expected && expected != length)
            throw new WorkerProtocolException($"buffer_result announced {expected} bytes but the PDF frame has {length}");
        return await Protocol.ReadPayloadAsync(_stdout, length, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool IsCommunicationFailure(Exception ex) =>
        ex is IOException or ObjectDisposedException or WorkerProtocolException or InvalidOperationException;

    // ------------------------------------------------------------------ termination

    /// <summary>Kill the worker (and anything it started). Never throws; safe after dispose.</summary>
    internal void Kill()
    {
        _faulted = true;
        try
        {
#if NET5_0_OR_GREATER
            _process.Kill(entireProcessTree: true);
#else
            _process.Kill();
#endif
        }
        catch
        {
            // Already exited, or the Process object is disposed.
        }
    }

    /// <summary>
    /// Stop the worker: wait up to <paramref name="grace"/> for a running conversion, ask it to
    /// quit (and close its stdin), and kill it if it hasn't exited when the grace period ends.
    /// Idempotent; concurrent callers all wait for the same shutdown.
    /// </summary>
    public Task DisposeAsync(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
            _ = DisposeCoreAsync(grace);
        return _disposed.Task;
    }

    private async Task DisposeCoreAsync(TimeSpan grace)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            bool acquired = await _busy.WaitAsync(grace).ConfigureAwait(false);
            if (!acquired)
            {
                // A conversion is still running: kill it, and let it report the failure.
                _killedByDispose = true;
                Kill();
                acquired = await _busy.WaitAsync(ExitInfoWait).ConfigureAwait(false);
            }

            try
            {
                if (!_faulted && !HasExited())
                {
                    try
                    {
                        var quit = Protocol.WriteAsync(_stdin, _writer.BuildQuit(), CancellationToken.None).AsTask();
                        await Task.WhenAny(quit, Task.Delay(Remaining(grace, clock))).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The worker is gone already.
                    }
                }
                try
                {
                    // End of stdin also tells the worker to exit.
                    _stdin.Dispose();
                }
                catch
                {
                    // Broken pipe: the worker is gone already.
                }

                if (!await WaitForExitAsync(Remaining(grace, clock)).ConfigureAwait(false))
                {
                    Kill();
                    await WaitForExitAsync(ExitInfoWait).ConfigureAwait(false);
                }
                await Task.WhenAny(_stderrPump, Task.Delay(StderrFlushWait)).ConfigureAwait(false);
            }
            finally
            {
                if (acquired)
                    _busy.Release();
                try
                {
                    _process.Dispose();
                }
                catch
                {
                    // Nothing left to release.
                }
            }
        }
        catch
        {
            Kill();
        }
        finally
        {
            _disposed.TrySetResult(true);
        }
    }

    private static TimeSpan Remaining(TimeSpan grace, Stopwatch clock)
    {
        var left = grace - clock.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private bool HasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Wait for the process to exit; false if it is still running after <paramref name="timeout"/>.</summary>
    private async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        try
        {
            if (_process.HasExited)
                return true;
            if (timeout <= TimeSpan.Zero)
                return false;
#if NET5_0_OR_GREATER
            using var cts = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
#else
            var ms = (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds);
            return await Task.Run(() => _process.WaitForExit(ms)).ConfigureAwait(false);
#endif
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return true; // disposed or not associated: nothing to wait for
        }
    }

    /// <summary>"Exit code N. Stderr: …" once the process has exited (bounded wait).</summary>
    private async Task<string> DescribeExitAsync()
    {
        string exit = "It is still running.";
        if (await WaitForExitAsync(ExitInfoWait).ConfigureAwait(false))
        {
            try
            {
                exit = $"Exit code: {_process.ExitCode.ToString(CultureInfo.InvariantCulture)}.";
            }
            catch
            {
                exit = "Exit code unavailable.";
            }
        }
        await Task.WhenAny(_stderrPump, Task.Delay(StderrFlushWait)).ConfigureAwait(false);
        var stderr = _stderr.ToString();
        return stderr.Length == 0 ? exit : $"{exit} Stderr: {stderr}";
    }

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds >= 1
            ? t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s"
            : t.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + " ms";

    /// <summary>
    /// Kills the worker when the timeout elapses or the caller's token is cancelled (the
    /// protocol has no way to cancel a conversion). The first reason wins; <see cref="Disarm"/>
    /// stops it once the answer has arrived.
    /// </summary>
    private sealed class AbortGuard : IDisposable
    {
        public const int None = 0;
        public const int TimedOut = 1;
        public const int Canceled = 2;
        private const int Disarmed = -1;

        private readonly WorkerProcess _owner;
        private readonly CancellationTokenSource? _timer;
        private CancellationTokenRegistration _timerRegistration;
        private CancellationTokenRegistration _cancelRegistration;
        private int _reason;

        public AbortGuard(WorkerProcess owner, TimeSpan timeout, CancellationToken ct)
        {
            _owner = owner;
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                _timer = new CancellationTokenSource(timeout);
                _timerRegistration = _timer.Token.Register(s => ((AbortGuard)s!).Abort(TimedOut), this);
            }
            if (ct.CanBeCanceled)
                _cancelRegistration = ct.Register(s => ((AbortGuard)s!).Abort(Canceled), this);
        }

        public int Reason => Volatile.Read(ref _reason);

        /// <summary>Stop aborting. False if an abort already happened.</summary>
        public bool Disarm() => Interlocked.CompareExchange(ref _reason, Disarmed, None) == None;

        private void Abort(int reason)
        {
            if (Interlocked.CompareExchange(ref _reason, reason, None) == None)
                _owner.Kill();
        }

        public void Dispose()
        {
            _cancelRegistration.Dispose();
            _timerRegistration.Dispose();
            _timer?.Dispose();
        }
    }
}
