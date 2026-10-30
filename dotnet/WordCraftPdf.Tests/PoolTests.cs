using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static WordCraftPdf.Tests.TestHelpers;

namespace WordCraftPdf.Tests;

/// <summary>
/// Pool and process behaviour, against the fake worker (WordCraftPdf.Tests.FakeWorker): crash,
/// timeout, cancellation, recycling, idle dispatch, start-up failures, dispose.
/// </summary>
public class PoolTests
{
    // ---------------------------------------------------------------- basic modes

    [Fact]
    public async Task Buffer_RoundTrip_ReturnsPdfAndPageCount()
    {
        await using var converter = CreateFake();
        var result = await converter.ConvertAsync(Bytes("hello"), DocumentFormat.Docx);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.PageCount);
        Assert.StartsWith("%PDF-FAKE", Encoding.ASCII.GetString(result.Data!));
        Assert.EndsWith("hello", Encoding.ASCII.GetString(result.Data!));
        Assert.StartsWith("FakeWorker 1.0", converter.Version);
    }

    [Fact]
    public async Task Buffer_LargePayload_RoundTripsExactly()
    {
        await using var converter = CreateFake();
        var doc = new byte[8 * 1024 * 1024 + 3];
        new Random(5).NextBytes(doc);
        doc[0] = (byte)'X'; // not a command
        var result = await converter.ConvertAsync(doc);
        Assert.True(result.Success, result.ErrorMessage);
        var prefix = Encoding.ASCII.GetBytes($"%PDF-FAKE pid={FakePid(result.Data!)}\n");
        Assert.Equal(prefix.Length + doc.Length, result.Data!.Length);
        Assert.True(result.Data.AsSpan(prefix.Length).SequenceEqual(doc));
    }

    [Fact]
    public async Task Buffer_SendsRequestFieldsAndOptions()
    {
        await using var converter = CreateFake();
        var options = new ConversionOptions { PageRange = "2-", TaggedPdf = true, Title = "T" };
        var result = await converter.ConvertAsync(Bytes("ECHO-REQUEST"), DocumentFormat.Odt, options);
        Assert.True(result.Success, result.ErrorMessage);
        using var json = JsonDocument.Parse(result.Data!);
        var root = json.RootElement;
        Assert.Equal("convert_buffer", root.GetProperty("type").GetString());
        Assert.Equal(3, root.GetProperty("format").GetInt32());
        Assert.Equal("ECHO-REQUEST".Length, root.GetProperty("data_size").GetInt32());
        Assert.Equal("{\"page_range\":\"2-\",\"tagged_pdf\":true,\"title\":\"T\"}", root.GetProperty("options").GetRawText());
    }

    [Fact]
    public async Task File_RoundTrip()
    {
        var dir = NewTempDir();
        try
        {
            var input = Path.Combine(dir, "in.docx");
            var output = Path.Combine(dir, "out.pdf");
            File.WriteAllText(input, "file content");
            await using var converter = CreateFake();
            var result = await converter.ConvertAsync(input, output);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(3, result.PageCount);
            Assert.EndsWith("file content", File.ReadAllText(output));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task File_SendsAbsolutePathsAndDetectedFormat()
    {
        var dir = NewTempDir();
        try
        {
            var input = Path.Combine(dir, "doc.RTF");
            File.WriteAllText(input, "ECHO-REQUEST");
            await using var converter = CreateFake();
            var output = Path.Combine(dir, "out.pdf");
            Assert.True(await converter.ConvertAsync(input, output));
            using var json = JsonDocument.Parse(File.ReadAllBytes(output));
            Assert.Equal("convert", json.RootElement.GetProperty("type").GetString());
            Assert.Equal(input, json.RootElement.GetProperty("input").GetString());
            Assert.Equal(output, json.RootElement.GetProperty("output").GetString());
            Assert.Equal(4, json.RootElement.GetProperty("format").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("options", out _));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Streams_AllOverloads()
    {
        var dir = NewTempDir();
        try
        {
            await using var converter = CreateFake();

            // MemoryStream → MemoryStream
            var output = new MemoryStream();
            var r1 = await converter.ConvertAsync(new MemoryStream(Bytes("ms")), output);
            Assert.True(r1.Success, r1.ErrorMessage);
            Assert.EndsWith("ms", Encoding.ASCII.GetString(output.ToArray()));

            // MemoryStream positioned mid-way: only the rest is sent
            var partial = new MemoryStream(Bytes("skip-ME"));
            partial.Position = 5;
            output = new MemoryStream();
            Assert.True(await converter.ConvertAsync(partial, output));
            Assert.EndsWith("\nME", Encoding.ASCII.GetString(output.ToArray()));
            Assert.Equal(partial.Length, partial.Position);

            // Seekable FileStream → file
            var input = Path.Combine(dir, "in.bin");
            File.WriteAllText(input, "from-file-stream");
            var pdfPath = Path.Combine(dir, "out.pdf");
            using (var fs = File.OpenRead(input))
                Assert.True(await converter.ConvertAsync(fs, pdfPath, DocumentFormat.Docx));
            Assert.EndsWith("from-file-stream", File.ReadAllText(pdfPath));

            // Non-seekable stream larger than the pooled chunk → stream
            var big = new byte[300_000];
            Array.Fill(big, (byte)'b');
            output = new MemoryStream();
            Assert.True(await converter.ConvertAsync(new NonSeekableStream(big), output));
            Assert.EndsWith(new string('b', 1000), Encoding.ASCII.GetString(output.ToArray()));
            Assert.Equal(big.Length, output.Length - output.ToArray().TakeWhile(b => b != '\n').Count() - 1);

            // File path → stream
            output = new MemoryStream();
            Assert.True(await converter.ConvertAsync(input, output));
            Assert.EndsWith("from-file-stream", Encoding.ASCII.GetString(output.ToArray()));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Failure_IsMappedFromWorker()
    {
        await using var converter = CreateFake();
        var result = await converter.ConvertAsync(Bytes("FAIL:11"));
        Assert.False(result.Success);
        Assert.Equal(WordCraftPdfErrorCode.PasswordRequired, result.ErrorCode);
        Assert.Equal("fake failure", result.ErrorMessage);
        Assert.Null(result.Data);
        Assert.Equal(0, result.PageCount);

        // A failed conversion does not cost the worker.
        var pid = converter.Pool.Workers.Single().ProcessId;
        var next = await converter.ConvertAsync(Bytes("ok"));
        Assert.True(next.Success);
        Assert.Equal(pid, FakePid(next.Data!));
    }

    [Fact]
    public async Task Diagnostics_AreMapped()
    {
        await using var converter = CreateFake();
        var result = await converter.ConvertAsync(Bytes("DIAG"));
        Assert.True(result.Success);
        Assert.True(result.HasFontWarnings);
        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("Calibri", d.FontName);
        Assert.Equal("Carlito", d.SubstitutedWith);
    }

    [Fact]
    public async Task StartupDiagnostics_AreExposed()
    {
        await using var converter = CreateFake(directives: "fake:bad-font-dir");
        Assert.Empty(converter.StartupDiagnostics);
        Assert.Null(converter.Version);
        await converter.WarmUpAsync();
        var d = Assert.Single(converter.StartupDiagnostics);
        Assert.Contains("font folder", d.Message);
        Assert.NotNull(converter.Version);
    }

    // ---------------------------------------------------------------- crash isolation

    [Theory]
    [InlineData("CRASH")]
    [InlineData("CRASH-PARTIAL")]
    public async Task Crash_ReturnsWorkerCrashed_AndNextConversionSucceeds(string command)
    {
        await using var converter = CreateFake();
        var first = await converter.ConvertAsync(Bytes("ok"));
        var firstPid = FakePid(first.Data!);

        var crashed = await converter.ConvertAsync(Bytes(command));
        Assert.False(crashed.Success);
        Assert.Equal(WordCraftPdfErrorCode.WorkerCrashed, crashed.ErrorCode);
        Assert.Contains("134", crashed.ErrorMessage);
        Assert.Equal(0, converter.Pool.LiveWorkerCount);

        var next = await converter.ConvertAsync(Bytes("ok"));
        Assert.True(next.Success, next.ErrorMessage);
        Assert.NotEqual(firstPid, FakePid(next.Data!));
    }

    [Fact]
    public async Task Crash_InFileMode_ReturnsWorkerCrashed()
    {
        var dir = NewTempDir();
        try
        {
            var input = Path.Combine(dir, "crash.docx");
            File.WriteAllText(input, "CRASH");
            await using var converter = CreateFake();
            var result = await converter.ConvertAsync(input, Path.Combine(dir, "out.pdf"));
            Assert.Equal(WordCraftPdfErrorCode.WorkerCrashed, result.ErrorCode);
            Assert.True(await converter.ConvertAsync(Bytes("ok")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task WorkerKilledWhileIdle_IsReplacedTransparently()
    {
        await using var converter = CreateFake(warmUp: true);
        var worker = converter.Pool.Workers.Single();
        using (var p = Process.GetProcessById(worker.ProcessId))
        {
            p.Kill();
            p.WaitForExit();
        }

        var result = await converter.ConvertAsync(Bytes("ok"));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEqual(worker.ProcessId, FakePid(result.Data!));
    }

    [Theory]
    [InlineData("ERROR", "fake protocol error")]
    [InlineData("BADID", "request")]
    public async Task ProtocolViolation_FailsAndReplacesWorker(string command, string expectedText)
    {
        await using var converter = CreateFake();
        var result = await converter.ConvertAsync(Bytes(command));
        Assert.False(result.Success);
        Assert.Equal(WordCraftPdfErrorCode.Unknown, result.ErrorCode);
        Assert.Contains(expectedText, result.ErrorMessage);
        Assert.True(await converter.ConvertAsync(Bytes("ok")));
    }

    // ---------------------------------------------------------------- timeout and cancellation

    [Fact]
    public async Task Timeout_ReturnsTimeoutResult_KillsWorker_AndNextConversionSucceeds()
    {
        await using var converter = CreateFake(timeout: TimeSpan.FromMilliseconds(500), warmUp: true);
        var pid = converter.Pool.Workers.Single().ProcessId;

        var sw = Stopwatch.StartNew();
        var result = await converter.ConvertAsync(Bytes("HANG"));
        sw.Stop();

        Assert.False(result.Success);
        Assert.Equal(WordCraftPdfErrorCode.Timeout, result.ErrorCode);
        Assert.Contains("500 ms", result.ErrorMessage);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));
        Assert.True(await WaitUntilAsync(() => !IsProcessRunning(pid), TimeSpan.FromSeconds(5)));

        var next = await converter.ConvertAsync(Bytes("ok"));
        Assert.True(next.Success, next.ErrorMessage);
        Assert.NotEqual(pid, FakePid(next.Data!));
    }

    [Fact]
    public async Task Timeout_InFileMode()
    {
        var dir = NewTempDir();
        try
        {
            var input = Path.Combine(dir, "hang.docx");
            File.WriteAllText(input, "HANG");
            await using var converter = CreateFake(timeout: TimeSpan.FromMilliseconds(300));
            var result = await converter.ConvertAsync(input, Path.Combine(dir, "out.pdf"));
            Assert.Equal(WordCraftPdfErrorCode.Timeout, result.ErrorCode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Cancellation_MidConversion_KillsWorker_AndThrows()
    {
        await using var converter = CreateFake(warmUp: true);
        var pid = converter.Pool.Workers.Single().ProcessId;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(Bytes("HANG"), cancellationToken: cts.Token));
        Assert.True(await WaitUntilAsync(() => !IsProcessRunning(pid), TimeSpan.FromSeconds(5)));

        var next = await converter.ConvertAsync(Bytes("ok"));
        Assert.True(next.Success, next.ErrorMessage);
    }

    [Fact]
    public async Task Cancellation_BeforeStart_ThrowsWithoutTouchingWorkers()
    {
        await using var converter = CreateFake(warmUp: true);
        var pid = converter.Pool.Workers.Single().ProcessId;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(Bytes("ok"), cancellationToken: cts.Token));
        Assert.True(IsProcessRunning(pid));
        var next = await converter.ConvertAsync(Bytes("ok"));
        Assert.Equal(pid, FakePid(next.Data!));
    }

    [Fact]
    public async Task Cancellation_WhileWaitingForAWorker_Throws()
    {
        await using var converter = CreateFake(maxWorkers: 1, warmUp: true);
        var busy = converter.ConvertAsync(Bytes("SLEEP:1500"));
        await WaitUntilAsync(() => converter.Pool.Workers.Any(w => w.IsBusy), TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(Bytes("ok"), cancellationToken: cts.Token));
        Assert.True((await busy).Success); // the running conversion was not affected
    }

    [Fact]
    public async Task Cancellation_AfterCompletion_DoesNotKillWorker()
    {
        await using var converter = CreateFake(warmUp: true);
        var pid = converter.Pool.Workers.Single().ProcessId;
        using var cts = new CancellationTokenSource();
        var result = await converter.ConvertAsync(Bytes("ok"), cancellationToken: cts.Token);
        cts.Cancel();
        Assert.True(result.Success);
        Assert.True(IsProcessRunning(pid));
        Assert.Equal(pid, FakePid((await converter.ConvertAsync(Bytes("ok"))).Data!));
    }

    // ---------------------------------------------------------------- recycling

    [Fact]
    public async Task Recycling_AfterMaxConversions_AllConversionsSucceed()
    {
        await using var converter = CreateFake(maxConversionsPerWorker: 2);
        var pids = new System.Collections.Generic.List<int>();
        for (int i = 0; i < 5; i++)
        {
            var result = await converter.ConvertAsync(Bytes("ok " + i));
            Assert.True(result.Success, result.ErrorMessage);
            pids.Add(FakePid(result.Data!));
        }
        Assert.Equal(pids[0], pids[1]);
        Assert.Equal(pids[2], pids[3]);
        Assert.Equal(3, pids.Distinct().Count());
        Assert.True(await WaitUntilAsync(() => !IsProcessRunning(pids[0]) && !IsProcessRunning(pids[2]), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Recycling_CountsFailedConversions()
    {
        await using var converter = CreateFake(maxConversionsPerWorker: 2);
        var a = await converter.ConvertAsync(Bytes("FAIL:2"));
        Assert.False(a.Success);
        var b = await converter.ConvertAsync(Bytes("ok"));
        var c = await converter.ConvertAsync(Bytes("ok"));
        Assert.True(b.Success && c.Success);
        Assert.NotEqual(FakePid(b.Data!), FakePid(c.Data!));
    }

    [Fact]
    public async Task IsAlive_IsSafeAfterDispose()
    {
        await using var converter = CreateFake(maxConversionsPerWorker: 1, warmUp: true);
        var worker = converter.Pool.Workers.Single();
        Assert.True((await converter.ConvertAsync(Bytes("ok"))).Success);
        await worker.DisposeAsync(TimeSpan.FromSeconds(5));
        Assert.False(worker.IsAlive);
        worker.Kill(); // also safe
    }

    // ---------------------------------------------------------------- dispatch

    [Fact]
    public async Task Workers_StartLazily()
    {
        await using var converter = CreateFake(maxWorkers: 4);
        Assert.Equal(0, converter.Pool.LiveWorkerCount);
        for (int i = 0; i < 5; i++)
            Assert.True(await converter.ConvertAsync(Bytes("ok")));
        Assert.Equal(1, converter.Pool.LiveWorkerCount); // sequential use never needs a second worker
    }

    [Fact]
    public async Task WarmUp_StartsAllWorkers()
    {
        await using var converter = CreateFake(maxWorkers: 3, warmUp: true);
        Assert.Equal(3, converter.Pool.LiveWorkerCount);
        Assert.Equal(3, converter.Pool.IdleWorkerCount);
        await converter.WarmUpAsync(); // idempotent
        Assert.Equal(3, converter.Pool.LiveWorkerCount);
    }

    [Fact]
    public async Task CreateAsync_WithWarmUp_StartsAllWorkers()
    {
        await using var converter = await PdfConverter.CreateAsync(new PdfConverterOptions
        {
            WorkerPath = FakeWorkerPath, MaxWorkers = 2, WarmUp = true
        });
        Assert.Equal(2, converter.Pool.LiveWorkerCount);
    }

    [Fact]
    public async Task Dispatch_PrefersIdleWorker_OverBusyOne()
    {
        await using var converter = CreateFake(maxWorkers: 2, warmUp: true);
        var slow = converter.ConvertAsync(Bytes("SLEEP:3000"));
        await WaitUntilAsync(() => converter.Pool.Workers.Any(w => w.IsBusy), TimeSpan.FromSeconds(5));
        var busyPid = converter.Pool.Workers.Single(w => w.IsBusy).ProcessId;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            var quick = await converter.ConvertAsync(Bytes("ok"));
            Assert.True(quick.Success);
            Assert.NotEqual(busyPid, FakePid(quick.Data!));
        }
        sw.Stop();
        Assert.False(slow.IsCompleted);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"quick conversions took {sw.Elapsed}");
        Assert.True((await slow).Success);
    }

    [Fact]
    public async Task Concurrency_16Conversions_4Workers()
    {
        await using var converter = CreateFake(maxWorkers: 4);
        var tasks = Enumerable.Range(0, 16).Select(i => converter.ConvertAsync(Bytes("SLEEP:100"))).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage));
        var pids = results.Select(r => FakePid(r.Data!)).Distinct().Count();
        Assert.InRange(pids, 2, 4);
        Assert.InRange(converter.Pool.LiveWorkerCount, 1, 4);
    }

    [Fact]
    public async Task Concurrency_NeverExceedsMaxWorkers()
    {
        await using var converter = CreateFake(maxWorkers: 3);
        int peak = 0;
        using var stop = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, converter.Pool.LiveWorkerCount);
                await Task.Delay(1);
            }
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            converter.ConvertAsync(Bytes(i % 5 == 0 ? "CRASH" : "SLEEP:20"))));
        stop.Cancel();
        await monitor;
        Assert.InRange(peak, 1, 3);
        Assert.Equal(6, results.Count(r => r.ErrorCode == WordCraftPdfErrorCode.WorkerCrashed));
        Assert.Equal(24, results.Count(r => r.Success));
    }

    // ---------------------------------------------------------------- start-up failures

    [Fact]
    public async Task InitError_ReturnsInitFailed()
    {
        await using var converter = CreateFake(directives: "fake:init-error");
        var result = await converter.ConvertAsync(Bytes("ok"));
        Assert.False(result.Success);
        Assert.Equal(WordCraftPdfErrorCode.InitFailed, result.ErrorCode);
        Assert.Contains("fake init failure", result.ErrorMessage);
        Assert.Equal(0, converter.Pool.LiveWorkerCount);
    }

    [Fact]
    public void InitError_WithWarmUp_ThrowsFromCreate()
    {
        var ex = Assert.Throws<WordCraftPdfException>(() => CreateFake(warmUp: true, directives: "fake:init-error"));
        Assert.Equal(WordCraftPdfErrorCode.InitFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task InitCrash_ReportsBoundedStderrTail()
    {
        await using var converter = CreateFake(directives: "fake:init-crash");
        var result = await converter.ConvertAsync(Bytes("ok"));
        Assert.Equal(WordCraftPdfErrorCode.InitFailed, result.ErrorCode);
        Assert.Contains("Exit code: 3", result.ErrorMessage);
        Assert.Contains("FAKE-STDERR-END", result.ErrorMessage);
        // ~200 KB were written; only the 64 KB tail is kept.
        Assert.InRange(result.ErrorMessage!.Length, 1000, Internal.WorkerProcess.StderrTailBytes + 1000);
    }

    [Fact]
    public async Task InitHang_FailsAfterStartupTimeout()
    {
        await using var converter = CreateFake(startupTimeout: TimeSpan.FromMilliseconds(500), directives: "fake:init-hang");
        var sw = Stopwatch.StartNew();
        var result = await converter.ConvertAsync(Bytes("ok"));
        Assert.Equal(WordCraftPdfErrorCode.InitFailed, result.ErrorCode);
        Assert.Contains("did not initialise", result.ErrorMessage);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task InitCancelled_Throws()
    {
        await using var converter = CreateFake(directives: "fake:init-hang");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(Bytes("ok"), cancellationToken: cts.Token));
        Assert.Equal(0, converter.Pool.LiveWorkerCount);
    }

    [Fact]
    public async Task WorkerThatIsNotExecutable_ReturnsInitFailed()
    {
        var dir = NewTempDir();
        try
        {
            var bogus = Path.Combine(dir, "not-a-worker");
            File.WriteAllText(bogus, "this is not an executable");
            await using var converter = PdfConverter.Create(new PdfConverterOptions { WorkerPath = bogus });
            var result = await converter.ConvertAsync(Bytes("ok"));
            Assert.Equal(WordCraftPdfErrorCode.InitFailed, result.ErrorCode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- dispose

    [Fact]
    public async Task Dispose_StopsIdleWorkersGracefully()
    {
        var converter = CreateFake(maxWorkers: 2, warmUp: true);
        var pids = converter.Pool.Workers.Select(w => w.ProcessId).ToArray();
        var sw = Stopwatch.StartNew();
        await converter.DisposeAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"dispose took {sw.Elapsed}");
        Assert.All(pids, pid => Assert.False(IsProcessRunning(pid)));
    }

    [Fact]
    public async Task Dispose_KillsWorkersThatIgnoreQuit()
    {
        var converter = CreateFake(shutdownTimeout: TimeSpan.FromMilliseconds(500), warmUp: true, directives: "fake:ignore-quit");
        var pid = converter.Pool.Workers.Single().ProcessId;
        var sw = Stopwatch.StartNew();
        converter.Dispose();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"dispose took {sw.Elapsed}");
        Assert.True(await WaitUntilAsync(() => !IsProcessRunning(pid), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Dispose_DuringConversion_FailsItAfterGracePeriod()
    {
        var converter = CreateFake(shutdownTimeout: TimeSpan.FromMilliseconds(300), warmUp: true);
        var hanging = converter.ConvertAsync(Bytes("HANG"));
        await WaitUntilAsync(() => converter.Pool.Workers.Any(w => w.IsBusy), TimeSpan.FromSeconds(5));
        await converter.DisposeAsync();
        var result = await hanging;
        Assert.False(result.Success);
        Assert.Equal(WordCraftPdfErrorCode.WorkerCrashed, result.ErrorCode);
        Assert.Contains("disposed", result.ErrorMessage);
    }

    [Fact]
    public async Task Dispose_LetsAShortConversionFinish()
    {
        var converter = CreateFake(shutdownTimeout: TimeSpan.FromSeconds(5), warmUp: true);
        var running = converter.ConvertAsync(Bytes("SLEEP:300"));
        await WaitUntilAsync(() => converter.Pool.Workers.Any(w => w.IsBusy), TimeSpan.FromSeconds(5));
        await converter.DisposeAsync();
        Assert.True((await running).Success);
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndBlocksFurtherUse()
    {
        var converter = CreateFake();
        await converter.DisposeAsync();
        await converter.DisposeAsync();
        converter.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => converter.ConvertAsync(Bytes("ok")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => converter.ConvertAsync("a.docx", "b.pdf"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => converter.WarmUpAsync());
    }

    [Fact]
    public async Task Dispose_WakesCallersWaitingForAWorker()
    {
        var converter = CreateFake(maxWorkers: 1, shutdownTimeout: TimeSpan.FromMilliseconds(200), warmUp: true);
        var running = converter.ConvertAsync(Bytes("HANG"));
        await WaitUntilAsync(() => converter.Pool.Workers.Any(w => w.IsBusy), TimeSpan.FromSeconds(5));
        var waiting = converter.ConvertAsync(Bytes("ok"));
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        await converter.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
        Assert.False((await running).Success);
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;
        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 7000));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Argument validation (no conversion reaches a worker).</summary>
public class ArgumentValidationTests
{
    [Fact]
    public void Create_RejectsBadOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfConverter.Create(new PdfConverterOptions { WorkerPath = FakeWorkerPath, MaxWorkers = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfConverter.Create(new PdfConverterOptions { WorkerPath = FakeWorkerPath, MaxConversionsPerWorker = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfConverter.Create(new PdfConverterOptions { WorkerPath = FakeWorkerPath, ConversionTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfConverter.Create(new PdfConverterOptions { WorkerPath = FakeWorkerPath, ConversionTimeout = TimeSpan.FromDays(30) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfConverter.Create(new PdfConverterOptions { WorkerPath = FakeWorkerPath, WorkerStartupTimeout = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentException>(() => PdfConverter.Create(new PdfConverterOptions { WorkerPath = FakeWorkerPath, FontDirectories = new[] { "/ok", "" } }));
        Assert.Throws<FileNotFoundException>(() => PdfConverter.Create(new PdfConverterOptions { WorkerPath = "/definitely/not/here/wcpdf_worker" }));
    }

    [Fact]
    public void Create_AcceptsInfiniteTimeout()
    {
        using var converter = PdfConverter.Create(new PdfConverterOptions
        {
            WorkerPath = FakeWorkerPath, ConversionTimeout = Timeout.InfiniteTimeSpan
        });
        Assert.Equal(Timeout.InfiniteTimeSpan, converter.Pool.ConversionTimeout);
    }

    [Fact]
    public async Task Convert_ValidatesArguments()
    {
        await using var converter = CreateFake();
        await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync("", "out.pdf"));
        await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync("in.docx", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync((string)null!, "out.pdf"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => converter.ConvertAsync((Stream)null!, new MemoryStream()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => converter.ConvertAsync(new MemoryStream(), (Stream)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => converter.ConvertAsync("in.docx", (Stream)null!));
        await Assert.ThrowsAsync<ArgumentException>(() => converter.ConvertAsync(new MemoryStream(), ""));
        Assert.Equal(0, converter.Pool.LiveWorkerCount);
    }

    [Fact]
    public async Task Convert_InvalidInputs_ReturnFailures()
    {
        await using var converter = CreateFake();

        var empty = await converter.ConvertAsync(ReadOnlyMemory<byte>.Empty);
        Assert.Equal(WordCraftPdfErrorCode.InvalidArgument, empty.ErrorCode);

        var badFormat = await converter.ConvertAsync(Bytes("x"), (DocumentFormat)42);
        Assert.Equal(WordCraftPdfErrorCode.InvalidFormat, badFormat.ErrorCode);

        var badStreamFormat = await converter.ConvertAsync(new MemoryStream(Bytes("x")), new MemoryStream(), (DocumentFormat)(-1));
        Assert.Equal(WordCraftPdfErrorCode.InvalidFormat, badStreamFormat.ErrorCode);

        var missing = await converter.ConvertAsync("/no/such/file.docx", "/tmp/out.pdf");
        Assert.Equal(WordCraftPdfErrorCode.FileNotFound, missing.ErrorCode);
        // The converter reports the full path (D:\no\such\file.docx on Windows).
        Assert.Contains(Path.GetFullPath("/no/such/file.docx"), missing.ErrorMessage);

        var missingToStream = await converter.ConvertAsync("/no/such/file.docx", new MemoryStream());
        Assert.Equal(WordCraftPdfErrorCode.FileNotFound, missingToStream.ErrorCode);

        var emptyStream = await converter.ConvertAsync(new MemoryStream(), new MemoryStream());
        Assert.Equal(WordCraftPdfErrorCode.InvalidArgument, emptyStream.ErrorCode);

        var unreadable = await converter.ConvertAsync(new UnreadableStream(), new MemoryStream());
        Assert.Equal(WordCraftPdfErrorCode.InvalidArgument, unreadable.ErrorCode);

        var unwritable = await converter.ConvertAsync(new MemoryStream(Bytes("x")), new MemoryStream(new byte[10], writable: false));
        Assert.Equal(WordCraftPdfErrorCode.InvalidArgument, unwritable.ErrorCode);

        var unwritable2 = await converter.ConvertAsync("in.docx", new MemoryStream(new byte[10], writable: false));
        Assert.Equal(WordCraftPdfErrorCode.InvalidArgument, unwritable2.ErrorCode);

        Assert.Equal(0, converter.Pool.LiveWorkerCount); // nothing reached a worker
    }

    [Fact]
    public async Task Convert_FailedConversion_DoesNotWriteOutput()
    {
        var dir = NewTempDir();
        try
        {
            await using var converter = CreateFake();
            var output = new MemoryStream();
            var result = await converter.ConvertAsync(new MemoryStream(Bytes("FAIL:2")), output);
            Assert.Equal(WordCraftPdfErrorCode.LoadFailed, result.ErrorCode);
            Assert.Equal(0, output.Length);

            var path = Path.Combine(dir, "out.pdf");
            var result2 = await converter.ConvertAsync(new MemoryStream(Bytes("FAIL:2")), path);
            Assert.False(result2.Success);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class UnreadableStream : MemoryStream
    {
        public override bool CanRead => false;
    }
}
