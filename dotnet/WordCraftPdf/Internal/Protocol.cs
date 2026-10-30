using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WordCraftPdf.Internal;

/// <summary>
/// Framing of the worker protocol (docs/protocol.md): every message is a 4-byte little-endian
/// unsigned length followed by that many bytes. JSON frames are UTF-8.
/// </summary>
internal static class Protocol
{
    /// <summary>Largest frame either side accepts (512 MiB).</summary>
    public const int MaxFrameSize = 512 * 1024 * 1024;

    /// <summary>Size of the length prefix.</summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// Read exactly <paramref name="count"/> bytes. Returns the number of bytes read, which is
    /// less than <paramref name="count"/> only at end of stream.
    /// </summary>
    public static async ValueTask<int> ReadExactAsync(
        Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
#if NET8_0_OR_GREATER
            int read = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct)
                .ConfigureAwait(false);
#else
            int read = await stream.ReadAsync(buffer, offset + total, count - total, ct)
                .ConfigureAwait(false);
#endif
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    /// <summary>
    /// Read a frame's length prefix into <paramref name="header"/> (a reusable 4-byte buffer).
    /// Returns -1 at end of stream (including a truncated prefix).
    /// </summary>
    /// <exception cref="WorkerProtocolException">The frame exceeds <see cref="MaxFrameSize"/>.</exception>
    public static async ValueTask<int> ReadFrameLengthAsync(Stream stream, byte[] header, CancellationToken ct)
    {
        int read = await ReadExactAsync(stream, header, 0, HeaderSize, ct).ConfigureAwait(false);
        if (read < HeaderSize)
            return -1;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxFrameSize)
            throw new WorkerProtocolException(
                $"Worker sent a frame of {length} bytes (limit {MaxFrameSize})");
        return (int)length;
    }

    /// <summary>
    /// Read one frame into a new, exactly-sized array. Returns null at end of stream (including
    /// a frame truncated by the worker's death).
    /// </summary>
    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, byte[] header, CancellationToken ct)
    {
        int length = await ReadFrameLengthAsync(stream, header, ct).ConfigureAwait(false);
        if (length < 0)
            return null;
        return await ReadPayloadAsync(stream, length, ct).ConfigureAwait(false);
    }

    /// <summary>Read a frame body of a known length into a new, exactly-sized array (null on EOF).</summary>
    public static async ValueTask<byte[]?> ReadPayloadAsync(Stream stream, int length, CancellationToken ct)
    {
        if (length == 0)
            return Array.Empty<byte>();
#if NET5_0_OR_GREATER
        var payload = GC.AllocateUninitializedArray<byte>(length);
#else
        var payload = new byte[length];
#endif
        int read = await ReadExactAsync(stream, payload, 0, length, ct).ConfigureAwait(false);
        return read == length ? payload : null;
    }

    /// <summary>Write a whole frame (prefix + payload) using a reusable 4-byte header buffer.</summary>
    public static async ValueTask WriteFrameAsync(
        Stream stream, ReadOnlyMemory<byte> payload, byte[] header, CancellationToken ct)
    {
        if (payload.Length > MaxFrameSize)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Frame exceeds {MaxFrameSize} bytes");
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await WriteAsync(stream, header, ct).ConfigureAwait(false);
        await WriteAsync(stream, payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Write raw bytes without copying them when the memory is array-backed.</summary>
    public static ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (data.IsEmpty)
            return default;
#if NET8_0_OR_GREATER
        return stream.WriteAsync(data, ct);
#else
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment))
            return new ValueTask(stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, ct));
        return WriteChunkedAsync(stream, data, ct);
#endif
    }

#if !NET8_0_OR_GREATER
    // Native or unmanaged memory on netstandard2.0: copy through one pooled chunk.
    private static async ValueTask WriteChunkedAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        const int ChunkSize = 81920;
        var chunk = ArrayPool<byte>.Shared.Rent(Math.Min(ChunkSize, data.Length));
        try
        {
            while (!data.IsEmpty)
            {
                int n = Math.Min(chunk.Length, data.Length);
                data.Span.Slice(0, n).CopyTo(chunk);
                await stream.WriteAsync(chunk, 0, n, ct).ConfigureAwait(false);
                data = data.Slice(n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
#endif

    /// <summary>Protocol <c>error_code</c> → SDK error code (unknown values → Unknown).</summary>
    public static WordCraftPdfErrorCode MapErrorCode(int? code) => code switch
    {
        1 => WordCraftPdfErrorCode.InitFailed,
        2 => WordCraftPdfErrorCode.LoadFailed,
        3 => WordCraftPdfErrorCode.ExportFailed,
        4 => WordCraftPdfErrorCode.InvalidFormat,
        5 => WordCraftPdfErrorCode.FileNotFound,
        6 => WordCraftPdfErrorCode.OutOfMemory,
        7 => WordCraftPdfErrorCode.PermissionDenied,
        9 => WordCraftPdfErrorCode.NotInitialized,
        10 => WordCraftPdfErrorCode.InvalidArgument,
        11 => WordCraftPdfErrorCode.PasswordRequired,
        12 => WordCraftPdfErrorCode.WrongPassword,
        _ => WordCraftPdfErrorCode.Unknown
    };
}

/// <summary>The worker broke the protocol (oversized frame, malformed JSON, out-of-step answer).</summary>
internal sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException(string message) : base(message) { }
    public WorkerProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Builds request frames (length prefix + UTF-8 JSON) into one reusable buffer, so a request
/// costs no allocation beyond buffer growth. Not thread-safe: one per worker, and a worker
/// handles one request at a time. The memory returned is valid until the next Build call.
/// </summary>
internal sealed class RequestWriter : IBufferWriter<byte>
{
    private byte[] _buffer;
    private int _written;
    private readonly Utf8JsonWriter _json;

    public RequestWriter(int initialCapacity = 1024)
    {
        _buffer = new byte[Math.Max(initialCapacity, 64)];
        _json = new Utf8JsonWriter(this);
    }

    /// <summary><c>{"type":"init","font_paths":[...]}</c> (font_paths omitted when empty).</summary>
    public ReadOnlyMemory<byte> BuildInit(IReadOnlyList<string>? fontPaths)
    {
        Begin();
        _json.WriteString("type", "init");
        if (fontPaths is { Count: > 0 })
        {
            _json.WriteStartArray("font_paths");
            for (int i = 0; i < fontPaths.Count; i++)
                _json.WriteStringValue(fontPaths[i]);
            _json.WriteEndArray();
        }
        return End(dataFrameSize: -1);
    }

    /// <summary><c>{"type":"convert","id":…,"input":…,"output":…,"format":…,"options":{…}}</c>.</summary>
    public ReadOnlyMemory<byte> BuildConvert(
        long id, string input, string output, DocumentFormat format, ConversionOptions? options)
    {
        Begin();
        _json.WriteString("type", "convert");
        _json.WriteNumber("id", id);
        _json.WriteString("input", input);
        _json.WriteString("output", output);
        _json.WriteNumber("format", (int)format);
        WriteOptions(options);
        return End(dataFrameSize: -1);
    }

    /// <summary>
    /// <c>{"type":"convert_buffer","id":…,"format":…,"data_size":…,"options":{…}}</c>, followed in
    /// the same buffer by the length prefix of the document frame, so the caller only has to
    /// write the document bytes after it.
    /// </summary>
    public ReadOnlyMemory<byte> BuildConvertBuffer(
        long id, DocumentFormat format, int dataSize, ConversionOptions? options)
    {
        Begin();
        _json.WriteString("type", "convert_buffer");
        _json.WriteNumber("id", id);
        _json.WriteNumber("format", (int)format);
        _json.WriteNumber("data_size", dataSize);
        WriteOptions(options);
        return End(dataFrameSize: dataSize);
    }

    /// <summary><c>{"type":"quit"}</c>.</summary>
    public ReadOnlyMemory<byte> BuildQuit()
    {
        Begin();
        _json.WriteString("type", "quit");
        return End(dataFrameSize: -1);
    }

    /// <summary>Only the options that differ from the protocol defaults are written.</summary>
    private void WriteOptions(ConversionOptions? options)
    {
        if (options is null)
            return;
        _json.WriteStartObject("options");
        if (options.PageRange is not null)
            _json.WriteString("page_range", options.PageRange);
        if (options.Password is not null)
            _json.WriteString("password", options.Password);
        if (options.TaggedPdf)
            _json.WriteBoolean("tagged_pdf", true);
        if (options.UpdateFields)
            _json.WriteBoolean("update_fields", true);
        if (options.IncludeMarkup)
            _json.WriteBoolean("include_markup", true);
        if (options.IncludeComments)
            _json.WriteBoolean("include_comments", true);
        if (options.Title is not null)
            _json.WriteString("title", options.Title);
        if (options.Author is not null)
            _json.WriteString("author", options.Author);
        _json.WriteEndObject();
    }

    private void Begin()
    {
        _written = Protocol.HeaderSize; // room for the length prefix
        _json.Reset(this);
        _json.WriteStartObject();
    }

    private ReadOnlyMemory<byte> End(int dataFrameSize)
    {
        _json.WriteEndObject();
        _json.Flush();
        int jsonLength = _written - Protocol.HeaderSize;
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(0, Protocol.HeaderSize), (uint)jsonLength);
        if (dataFrameSize >= 0)
        {
            var span = GetSpan(Protocol.HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)dataFrameSize);
            Advance(Protocol.HeaderSize);
        }
        return new ReadOnlyMemory<byte>(_buffer, 0, _written);
    }

    void IBufferWriter<byte>.Advance(int count) => Advance(count);

    private void Advance(int count) => _written += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1)
            sizeHint = 256;
        if (_buffer.Length - _written >= sizeHint)
            return;
        var bigger = new byte[Math.Max(_buffer.Length * 2, _written + sizeHint)];
        Buffer.BlockCopy(_buffer, 0, bigger, 0, _written);
        _buffer = bigger;
    }
}

/// <summary>A parsed worker response (<c>ready</c>, <c>result</c>, <c>buffer_result</c> or <c>error</c>).</summary>
internal sealed class WorkerResponse
{
    private static readonly IReadOnlyList<ConversionDiagnostic> NoDiagnostics = Array.Empty<ConversionDiagnostic>();

    public string? Type { get; private set; }
    public long? Id { get; private set; }
    public bool Success { get; private set; }
    public int? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public int PageCount { get; private set; }
    public long? DataSize { get; private set; }
    public string? Message { get; private set; }
    public string? Version { get; private set; }
    public IReadOnlyList<ConversionDiagnostic> Diagnostics { get; private set; } = NoDiagnostics;

    /// <exception cref="WorkerProtocolException">Not a JSON object with a string <c>type</c>.</exception>
    public static WorkerResponse Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
                throw new WorkerProtocolException("Worker response has no \"type\"");

            var r = new WorkerResponse { Type = type.GetString() };
            foreach (var p in root.EnumerateObject())
            {
                var v = p.Value;
                switch (p.Name)
                {
                    case "id" when v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var id):
                        r.Id = id;
                        break;
                    case "success" when v.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        r.Success = v.GetBoolean();
                        break;
                    case "error_code" when v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var code):
                        r.ErrorCode = code;
                        break;
                    case "error_message" when v.ValueKind == JsonValueKind.String:
                        r.ErrorMessage = v.GetString();
                        break;
                    case "page_count" when v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var pages):
                        r.PageCount = pages;
                        break;
                    case "data_size" when v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var size):
                        r.DataSize = size;
                        break;
                    case "message" when v.ValueKind == JsonValueKind.String:
                        r.Message = v.GetString();
                        break;
                    case "version" when v.ValueKind == JsonValueKind.String:
                        r.Version = v.GetString();
                        break;
                    case "diagnostics" when v.ValueKind == JsonValueKind.Array:
                        r.Diagnostics = ParseDiagnostics(v);
                        break;
                }
            }
            return r;
        }
        catch (JsonException ex)
        {
            throw new WorkerProtocolException("Worker sent malformed JSON: " + ex.Message, ex);
        }
    }

    internal static IReadOnlyList<ConversionDiagnostic> ParseDiagnostics(JsonElement array)
    {
        int count = array.GetArrayLength();
        if (count == 0)
            return NoDiagnostics;
        var list = new List<ConversionDiagnostic>(count);
        foreach (var d in array.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object)
                continue;
            var severity = DiagnosticSeverity.Info;
            var category = DiagnosticCategory.General;
            string message = "";
            string? font = null, substitutedWith = null;
            foreach (var p in d.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                    continue;
                var s = p.Value.GetString();
                switch (p.Name)
                {
                    case "severity":
                        severity = string.Equals(s, "warning", StringComparison.OrdinalIgnoreCase)
                            ? DiagnosticSeverity.Warning
                            : DiagnosticSeverity.Info;
                        break;
                    case "category":
                        category = string.Equals(s, "font", StringComparison.OrdinalIgnoreCase)
                            ? DiagnosticCategory.Font
                            : DiagnosticCategory.General;
                        break;
                    case "message":
                        message = s ?? "";
                        break;
                    case "font":
                        font = s;
                        break;
                    case "substituted_with":
                        substitutedWith = s;
                        break;
                }
            }
            list.Add(new ConversionDiagnostic(severity, category, message, font, substitutedWith));
        }
        return list;
    }
}
