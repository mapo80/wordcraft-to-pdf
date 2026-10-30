using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WordCraftPdf.Internal;

/// <summary>
/// Thread-safe pool of persistent <c>wcpdf_worker</c> processes.
///
/// <para><b>Dispatch.</b> A semaphore with <c>maxWorkers</c> slots bounds the conversions in
/// flight. A caller holding a slot takes an <b>idle</b> worker if there is one (most recently
/// used first, so hot workers stay hot) and only starts a new worker when none is idle. A
/// request therefore never queues behind a busy worker while another one is idle, and workers
/// are started lazily, never more than <c>maxWorkers</c>: every worker is either idle or owned
/// by exactly one slot holder, and a new one is only started when none is idle.</para>
///
/// <para><b>Recovery.</b> A worker that crashed, timed out, was cancelled or broke the protocol
/// is discarded; the next request starts a fresh one. A worker that served
/// <c>maxConversionsPerWorker</c> conversions is retired in the background.</para>
/// </summary>
internal sealed class WorkerPool : IAsyncDisposable
{
    private readonly string _workerPath;
    private readonly IReadOnlyList<string>? _fontPaths;
    private readonly int _maxWorkers;
    private readonly int _maxConversionsPerWorker;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentStack<WorkerProcess> _idle = new();
    private readonly ConcurrentDictionary<WorkerProcess, byte> _live = new();
    private readonly ConcurrentDictionary<Task, byte> _retiring = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private volatile bool _disposed;
    private long _requestId;
    private volatile string? _version;
    private volatile IReadOnlyList<ConversionDiagnostic> _startupDiagnostics = Array.Empty<ConversionDiagnostic>();

    public WorkerPool(
        string workerPath,
        IReadOnlyList<string>? fontPaths,
        int maxWorkers,
        int maxConversionsPerWorker,
        TimeSpan conversionTimeout,
        TimeSpan startupTimeout,
        TimeSpan shutdownTimeout)
    {
        _workerPath = workerPath;
        _fontPaths = fontPaths;
        _maxWorkers = maxWorkers;
        _maxConversionsPerWorker = maxConversionsPerWorker;
        ConversionTimeout = conversionTimeout;
        _startupTimeout = startupTimeout;
        _shutdownTimeout = shutdownTimeout;
        _slots = new SemaphoreSlim(maxWorkers, maxWorkers);
    }

    /// <summary>Per-conversion timeout. Settable so tests can change it between conversions.</summary>
    internal TimeSpan ConversionTimeout { get; set; }

    public int MaxWorkers => _maxWorkers;

    /// <summary>Version reported by the most recently started worker.</summary>
    public string? Version => _version;

    /// <summary>Diagnostics reported by the most recently started worker's <c>ready</c>.</summary>
    public IReadOnlyList<ConversionDiagnostic> StartupDiagnostics => _startupDiagnostics;

    /// <summary>Workers currently running (idle or busy).</summary>
    internal int LiveWorkerCount => _live.Count;

    /// <summary>Workers currently idle.</summary>
    internal int IdleWorkerCount => _idle.Count;

    /// <summary>Snapshot of the running workers (tests).</summary>
    internal IReadOnlyList<WorkerProcess> Workers => _live.Keys.ToList();

    // ------------------------------------------------------------------ warm-up

    /// <summary>Start workers until <c>maxWorkers</c> are running.</summary>
    /// <exception cref="WordCraftPdfException">A worker could not be started.</exception>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        // Holding every slot means no conversion runs meanwhile, so all live workers are idle
        // and the bound is respected exactly.
        int held = 0;
        try
        {
            for (; held < _maxWorkers; held++)
                await AcquireSlotAsync(ct).ConfigureAwait(false);

            int missing = _maxWorkers - _live.Count;
            if (missing <= 0)
                return;
            var starts = new Task<WorkerProcess>[missing];
            for (int i = 0; i < missing; i++)
                starts[i] = StartWorkerAsync(ct);
            try
            {
                await Task.WhenAll(starts).ConfigureAwait(false);
            }
            finally
            {
                foreach (var t in starts)
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                        Return(t.Result);
                }
            }
        }
        finally
        {
            if (held > 0)
                _slots.Release(held);
        }
    }

    // ------------------------------------------------------------------ conversions

    public Task<ConversionResult> ExecuteFileAsync(
        string input, string output, DocumentFormat format, ConversionOptions? options, CancellationToken ct) =>
        ExecuteAsync(
            (worker, id, timeout, token) => worker.ConvertFileAsync(id, input, output, format, options, timeout, token),
            (message, code) => ConversionResult.Fail(message, code, null),
            ct);

    public Task<ConversionResult<byte[]>> ExecuteBufferAsync(
        ReadOnlyMemory<byte> data, DocumentFormat format, ConversionOptions? options, CancellationToken ct) =>
        ExecuteAsync(
            (worker, id, timeout, token) => worker.ConvertBufferAsync(id, format, data, options, timeout, token),
            (message, code) => ConversionResult<byte[]>.Fail(message, code, null),
            ct);

    private async Task<TResult> ExecuteAsync<TResult>(
        Func<WorkerProcess, long, TimeSpan, CancellationToken, Task<TResult?>> convert,
        Func<string, WordCraftPdfErrorCode, TResult> fail,
        CancellationToken ct)
        where TResult : ConversionResult
    {
        await AcquireSlotAsync(ct).ConfigureAwait(false);
        try
        {
            // A worker found dead before the request could be sent (e.g. killed while idle) is
            // replaced and the request retried once: it never saw the document.
            for (int attempt = 0; ; attempt++)
            {
                WorkerProcess worker;
                try
                {
                    worker = await RentAsync(ct).ConfigureAwait(false);
                }
                catch (WordCraftPdfException ex)
                {
                    return fail(ex.Message, ex.ErrorCode);
                }

                TResult? result;
                try
                {
                    var id = Interlocked.Increment(ref _requestId);
                    result = await convert(worker, id, ConversionTimeout, ct).ConfigureAwait(false);
                }
                finally
                {
                    Return(worker);
                }

                if (result is not null)
                    return result;
                if (attempt >= 1)
                    return fail("The worker process exited before the request could be sent.",
                        WordCraftPdfErrorCode.WorkerCrashed);
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    // ------------------------------------------------------------------ slots and workers

    private async Task AcquireSlotAsync(CancellationToken ct)
    {
        ThrowHelpers.ThrowIfDisposed(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (!_slots.Wait(0))
        {
            try
            {
                if (ct.CanBeCanceled)
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
                    await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                else
                {
                    await _slots.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(PdfConverter));
            }
        }
        if (_disposed)
        {
            _slots.Release();
            throw new ObjectDisposedException(nameof(PdfConverter));
        }
    }

    /// <summary>An idle worker, or a new one if none is idle. The caller holds a slot.</summary>
    private async Task<WorkerProcess> RentAsync(CancellationToken ct)
    {
        while (_idle.TryPop(out var worker))
        {
            if (worker.IsAlive)
                return worker;
            Retire(worker); // died while idle
        }
        return await StartWorkerAsync(ct).ConfigureAwait(false);
    }

    private async Task<WorkerProcess> StartWorkerAsync(CancellationToken ct)
    {
        var worker = await WorkerProcess.StartAsync(_workerPath, _fontPaths, _startupTimeout, ct).ConfigureAwait(false);
        _live[worker] = 0;
        _version = worker.Version;
        _startupDiagnostics = worker.StartupDiagnostics;
        return worker;
    }

    /// <summary>Give a worker back after a conversion: keep it idle, or retire it.</summary>
    private void Return(WorkerProcess worker)
    {
        if (_disposed || !worker.IsAlive
            || (_maxConversionsPerWorker > 0 && worker.ConversionCount >= _maxConversionsPerWorker))
        {
            Retire(worker);
            return;
        }
        _idle.Push(worker);
        if (_disposed)
            DrainIdle(); // lost a race with DisposeAsync
    }

    /// <summary>Remove a worker from the pool and shut it down in the background.</summary>
    private void Retire(WorkerProcess worker)
    {
        if (!_live.TryRemove(worker, out _))
            return;
        var task = worker.DisposeAsync(_shutdownTimeout);
        if (task.IsCompleted)
            return;
        _retiring[task] = 0;
        _ = task.ContinueWith(
            (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
            _retiring, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void DrainIdle()
    {
        while (_idle.TryPop(out var worker))
            Retire(worker);
    }

    /// <summary>
    /// Shut every worker down: idle ones are asked to quit; busy ones get the shutdown grace
    /// period to finish their conversion, then are killed. Returns when all have exited.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _disposeCts.Cancel();

        DrainIdle();
        var shutdowns = new List<Task>();
        foreach (var worker in _live.Keys)
            shutdowns.Add(worker.DisposeAsync(_shutdownTimeout));
        shutdowns.AddRange(_retiring.Keys);
        await Task.WhenAll(shutdowns).ConfigureAwait(false);
    }
}
