using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WordCraftPdf.Internal;
using Xunit;

namespace WordCraftPdf.Tests;

public class FramingTests
{
    private static byte[] Frame(byte[] payload)
    {
        var f = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(f, (uint)payload.Length);
        payload.CopyTo(f, 4);
        return f;
    }

    [Fact]
    public async Task WriteFrame_ThenReadFrame_RoundTrips()
    {
        var ms = new MemoryStream();
        var header = new byte[4];
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"quit\"}");
        await Protocol.WriteFrameAsync(ms, payload, header, CancellationToken.None);

        Assert.Equal(Frame(payload), ms.ToArray());
        ms.Position = 0;
        var read = await Protocol.ReadFrameAsync(ms, header, CancellationToken.None);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task LengthPrefix_IsLittleEndian()
    {
        var ms = new MemoryStream();
        await Protocol.WriteFrameAsync(ms, new byte[0x0102], new byte[4], CancellationToken.None);
        var bytes = ms.ToArray();
        Assert.Equal(new byte[] { 0x02, 0x01, 0x00, 0x00 }, bytes.Take(4).ToArray());
    }

    [Fact]
    public async Task ReadFrame_EmptyStream_ReturnsNull()
    {
        Assert.Null(await Protocol.ReadFrameAsync(new MemoryStream(), new byte[4], CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_TruncatedPrefix_ReturnsNull()
    {
        Assert.Null(await Protocol.ReadFrameAsync(new MemoryStream(new byte[] { 5, 0 }), new byte[4], CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_TruncatedPayload_ReturnsNull()
    {
        var bytes = Frame(new byte[10]).Take(9).ToArray();
        Assert.Null(await Protocol.ReadFrameAsync(new MemoryStream(bytes), new byte[4], CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_OversizedFrame_ThrowsProtocolException()
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)Protocol.MaxFrameSize + 1);
        await Assert.ThrowsAsync<WorkerProtocolException>(async () =>
            await Protocol.ReadFrameAsync(new MemoryStream(bytes), new byte[4], CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_MaxFrameSizeIs512MiB()
    {
        Assert.Equal(512 * 1024 * 1024, Protocol.MaxFrameSize);
        // A frame of exactly the limit is accepted (length check only; truncated body → null).
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)Protocol.MaxFrameSize);
        Assert.Equal(Protocol.MaxFrameSize, await Protocol.ReadFrameLengthAsync(new MemoryStream(bytes), new byte[4], CancellationToken.None));
    }

    [Fact]
    public async Task ReadFrame_EmptyPayload_ReturnsEmptyArray()
    {
        var read = await Protocol.ReadFrameAsync(new MemoryStream(Frame(Array.Empty<byte>())), new byte[4], CancellationToken.None);
        Assert.NotNull(read);
        Assert.Empty(read!);
    }

    [Fact]
    public async Task ReadFrame_HandlesPartialReads()
    {
        var payload = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        var stream = new TrickleStream(Frame(payload), maxChunk: 7);
        var read = await Protocol.ReadFrameAsync(stream, new byte[4], CancellationToken.None);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task ReadFrame_MultipleFramesInSequence()
    {
        var ms = new MemoryStream();
        var header = new byte[4];
        for (int i = 0; i < 5; i++)
            await Protocol.WriteFrameAsync(ms, Encoding.UTF8.GetBytes("frame" + i), header, CancellationToken.None);
        ms.Position = 0;
        for (int i = 0; i < 5; i++)
            Assert.Equal("frame" + i, Encoding.UTF8.GetString((await Protocol.ReadFrameAsync(ms, header, CancellationToken.None))!));
        Assert.Null(await Protocol.ReadFrameAsync(ms, header, CancellationToken.None));
    }

    [Fact]
    public async Task ReadPayload_ReturnsExactlySizedArray()
    {
        var payload = new byte[12345];
        new Random(1).NextBytes(payload);
        var read = await Protocol.ReadFrameAsync(new MemoryStream(Frame(payload)), new byte[4], CancellationToken.None);
        Assert.Equal(payload.Length, read!.Length);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Write_LargePayload_RoundTrips()
    {
        var payload = new byte[3 * 1024 * 1024 + 17];
        new Random(2).NextBytes(payload);
        var ms = new MemoryStream();
        await Protocol.WriteFrameAsync(ms, payload, new byte[4], CancellationToken.None);
        ms.Position = 0;
        Assert.Equal(payload, await Protocol.ReadFrameAsync(ms, new byte[4], CancellationToken.None));
    }

    [Fact]
    public async Task Write_SliceOfArray_WritesOnlyTheSlice()
    {
        var array = Encoding.ASCII.GetBytes("xxHELLOxx");
        var ms = new MemoryStream();
        await Protocol.WriteFrameAsync(ms, new ReadOnlyMemory<byte>(array, 2, 5), new byte[4], CancellationToken.None);
        Assert.Equal(Frame(Encoding.ASCII.GetBytes("HELLO")), ms.ToArray());
    }

    [Fact]
    public async Task Write_NonArrayMemory_IsWritten()
    {
        using var native = new NativeMemoryManager(100_000);
        var expected = native.Fill();
        var ms = new MemoryStream();
        await Protocol.WriteFrameAsync(ms, native.Memory, new byte[4], CancellationToken.None);
        ms.Position = 0;
        Assert.Equal(expected, await Protocol.ReadFrameAsync(ms, new byte[4], CancellationToken.None));
    }

    [Theory]
    [InlineData(1, WordCraftPdfErrorCode.InitFailed)]
    [InlineData(2, WordCraftPdfErrorCode.LoadFailed)]
    [InlineData(3, WordCraftPdfErrorCode.ExportFailed)]
    [InlineData(4, WordCraftPdfErrorCode.InvalidFormat)]
    [InlineData(5, WordCraftPdfErrorCode.FileNotFound)]
    [InlineData(6, WordCraftPdfErrorCode.OutOfMemory)]
    [InlineData(7, WordCraftPdfErrorCode.PermissionDenied)]
    [InlineData(9, WordCraftPdfErrorCode.NotInitialized)]
    [InlineData(10, WordCraftPdfErrorCode.InvalidArgument)]
    [InlineData(11, WordCraftPdfErrorCode.PasswordRequired)]
    [InlineData(12, WordCraftPdfErrorCode.WrongPassword)]
    [InlineData(99, WordCraftPdfErrorCode.Unknown)]
    [InlineData(8, WordCraftPdfErrorCode.Unknown)]
    [InlineData(100, WordCraftPdfErrorCode.Unknown)]
    [InlineData(-1, WordCraftPdfErrorCode.Unknown)]
    [InlineData(null, WordCraftPdfErrorCode.Unknown)]
    public void MapErrorCode_MapsProtocolCodes(int? code, WordCraftPdfErrorCode expected)
    {
        Assert.Equal(expected, Protocol.MapErrorCode(code));
    }

    /// <summary>Returns at most maxChunk bytes per read.</summary>
    private sealed class TrickleStream : MemoryStream
    {
        private readonly int _max;
        public TrickleStream(byte[] data, int maxChunk) : base(data) => _max = maxChunk;
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, _max));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            base.ReadAsync(buffer, offset, Math.Min(count, _max), ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer.Slice(0, Math.Min(buffer.Length, _max)), ct);
    }

    /// <summary>Memory not backed by a managed array.</summary>
    private sealed unsafe class NativeMemoryManager : MemoryManager<byte>
    {
        private readonly IntPtr _ptr;
        private readonly int _length;
        public NativeMemoryManager(int length)
        {
            _length = length;
            _ptr = Marshal.AllocHGlobal(length);
        }
        public override Span<byte> GetSpan() => new((void*)_ptr, _length);
        public byte[] Fill()
        {
            var span = GetSpan();
            for (int i = 0; i < span.Length; i++)
                span[i] = (byte)(i * 7);
            return span.ToArray();
        }
        public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)_ptr + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => Marshal.FreeHGlobal(_ptr);
    }
}

public class RequestWriterTests
{
    private static (JsonDocument json, byte[] frame) Parse(ReadOnlyMemory<byte> frame, int trailing = 0)
    {
        var bytes = frame.ToArray();
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        Assert.Equal(bytes.Length - 4 - trailing, (int)length);
        return (JsonDocument.Parse(bytes.AsMemory(4, (int)length)), bytes);
    }

    [Fact]
    public void Init_WithFontPaths()
    {
        var (json, _) = Parse(new RequestWriter().BuildInit(new[] { "/opt/fonts", "/srv/brand fonts" }));
        var root = json.RootElement;
        Assert.Equal("init", root.GetProperty("type").GetString());
        Assert.Equal(new[] { "/opt/fonts", "/srv/brand fonts" },
            root.GetProperty("font_paths").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void Init_WithoutFontPaths_OmitsField()
    {
        var (json, _) = Parse(new RequestWriter().BuildInit(null));
        Assert.Equal("{\"type\":\"init\"}", json.RootElement.GetRawText());
        var (json2, _) = Parse(new RequestWriter().BuildInit(Array.Empty<string>()));
        Assert.False(json2.RootElement.TryGetProperty("font_paths", out _));
    }

    [Fact]
    public void Convert_WithoutOptions()
    {
        var (json, _) = Parse(new RequestWriter().BuildConvert(7, "/abs/in.docx", "/abs/out.pdf", DocumentFormat.Docx, null));
        var root = json.RootElement;
        Assert.Equal("convert", root.GetProperty("type").GetString());
        Assert.Equal(7, root.GetProperty("id").GetInt64());
        Assert.Equal("/abs/in.docx", root.GetProperty("input").GetString());
        Assert.Equal("/abs/out.pdf", root.GetProperty("output").GetString());
        Assert.Equal(1, root.GetProperty("format").GetInt32());
        Assert.False(root.TryGetProperty("options", out _));
    }

    [Fact]
    public void Convert_AllOptions_AreSnakeCase()
    {
        var options = new ConversionOptions
        {
            PageRange = "1-3,5,8-",
            Password = "s3cret",
            TaggedPdf = true,
            UpdateFields = true,
            IncludeMarkup = true,
            IncludeComments = true,
            Title = "Title",
            Author = "Author"
        };
        var (json, _) = Parse(new RequestWriter().BuildConvert(1, "a", "b", DocumentFormat.Unknown, options));
        var o = json.RootElement.GetProperty("options");
        Assert.Equal(
            new[] { "page_range", "password", "tagged_pdf", "update_fields", "include_markup", "include_comments", "title", "author" },
            o.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("1-3,5,8-", o.GetProperty("page_range").GetString());
        Assert.Equal("s3cret", o.GetProperty("password").GetString());
        Assert.True(o.GetProperty("tagged_pdf").GetBoolean());
        Assert.True(o.GetProperty("update_fields").GetBoolean());
        Assert.True(o.GetProperty("include_markup").GetBoolean());
        Assert.True(o.GetProperty("include_comments").GetBoolean());
        Assert.Equal("Title", o.GetProperty("title").GetString());
        Assert.Equal("Author", o.GetProperty("author").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("format").GetInt32());
    }

    [Fact]
    public void Convert_DefaultOptions_WritesEmptyObject()
    {
        var (json, _) = Parse(new RequestWriter().BuildConvert(1, "a", "b", DocumentFormat.Docx, new ConversionOptions()));
        Assert.Equal("{}", json.RootElement.GetProperty("options").GetRawText());
    }

    [Fact]
    public void Convert_PartialOptions_OmitsDefaults()
    {
        var (json, _) = Parse(new RequestWriter().BuildConvert(1, "a", "b", DocumentFormat.Docx,
            new ConversionOptions { TaggedPdf = true, Author = "me" }));
        Assert.Equal("{\"tagged_pdf\":true,\"author\":\"me\"}", json.RootElement.GetProperty("options").GetRawText());
    }

    [Fact]
    public void ConvertBuffer_IsFollowedByTheDocumentFramePrefix()
    {
        var frame = new RequestWriter().BuildConvertBuffer(42, DocumentFormat.Rtf, 48213, new ConversionOptions { PageRange = "2" });
        var (json, bytes) = Parse(frame, trailing: 4);
        var root = json.RootElement;
        Assert.Equal("convert_buffer", root.GetProperty("type").GetString());
        Assert.Equal(42, root.GetProperty("id").GetInt64());
        Assert.Equal(4, root.GetProperty("format").GetInt32());
        Assert.Equal(48213, root.GetProperty("data_size").GetInt64());
        Assert.Equal("2", root.GetProperty("options").GetProperty("page_range").GetString());
        Assert.Equal(48213u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4)));
    }

    [Fact]
    public void Quit()
    {
        var (json, _) = Parse(new RequestWriter().BuildQuit());
        Assert.Equal("{\"type\":\"quit\"}", json.RootElement.GetRawText());
    }

    [Fact]
    public void Writer_IsReusableAndGrows()
    {
        var writer = new RequestWriter(initialCapacity: 64);
        var longPath = "/" + new string('p', 10_000) + "/in.docx";
        var (big, _) = Parse(writer.BuildConvert(1, longPath, "/out.pdf", DocumentFormat.Docx, null));
        Assert.Equal(longPath, big.RootElement.GetProperty("input").GetString());
        var (small, _) = Parse(writer.BuildQuit());
        Assert.Equal("quit", small.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Strings_AreEscapedAndRoundTrip()
    {
        var path = "C:\\Users\\Zoë\\\"quoted\"\\文書.docx";
        var title = "Line1\nLine2 \u2603 <tag> & 'apos'";
        var (json, _) = Parse(new RequestWriter().BuildConvert(1, path, "o.pdf", DocumentFormat.Docx, new ConversionOptions { Title = title }));
        Assert.Equal(path, json.RootElement.GetProperty("input").GetString());
        Assert.Equal(title, json.RootElement.GetProperty("options").GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(DocumentFormat.Unknown, 0)]
    [InlineData(DocumentFormat.Docx, 1)]
    [InlineData(DocumentFormat.Doc, 2)]
    [InlineData(DocumentFormat.Odt, 3)]
    [InlineData(DocumentFormat.Rtf, 4)]
    [InlineData(DocumentFormat.Html, 5)]
    [InlineData(DocumentFormat.Markdown, 6)]
    [InlineData(DocumentFormat.Text, 7)]
    public void Format_IsWrittenAsProtocolCode(DocumentFormat format, int code)
    {
        var (json, _) = Parse(new RequestWriter().BuildConvertBuffer(1, format, 1, null), trailing: 4);
        Assert.Equal(code, json.RootElement.GetProperty("format").GetInt32());
    }
}

public class WorkerResponseTests
{
    private static WorkerResponse Parse(string json) => WorkerResponse.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Ready()
    {
        var r = Parse("{\"type\":\"ready\",\"version\":\"WordCraftPdf 0.1.0 (WordCraft v0.12.0)\",\"diagnostics\":[{\"severity\":\"warning\",\"category\":\"general\",\"message\":\"font folder not found: /x\"}]}");
        Assert.Equal("ready", r.Type);
        Assert.Equal("WordCraftPdf 0.1.0 (WordCraft v0.12.0)", r.Version);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(DiagnosticCategory.General, d.Category);
        Assert.Equal("font folder not found: /x", d.Message);
    }

    [Fact]
    public void Result_Success()
    {
        var r = Parse("{\"type\":\"result\",\"id\":1,\"success\":true,\"error_code\":null,\"error_message\":null,\"page_count\":14,\"diagnostics\":[{\"severity\":\"warning\",\"category\":\"font\",\"message\":\"font \\\"Calibri\\\" is not available; substituted with \\\"Carlito\\\"\",\"font\":\"Calibri\",\"substituted_with\":\"Carlito\"}]}");
        Assert.Equal("result", r.Type);
        Assert.Equal(1, r.Id);
        Assert.True(r.Success);
        Assert.Null(r.ErrorCode);
        Assert.Null(r.ErrorMessage);
        Assert.Equal(14, r.PageCount);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(DiagnosticCategory.Font, d.Category);
        Assert.Equal("Calibri", d.FontName);
        Assert.Equal("Carlito", d.SubstitutedWith);
        Assert.Contains("Calibri", d.Message);
    }

    [Fact]
    public void Result_Failure()
    {
        var r = Parse("{\"type\":\"result\",\"id\":3,\"success\":false,\"error_code\":2,\"error_message\":\"bad zip\",\"diagnostics\":[]}");
        Assert.False(r.Success);
        Assert.Equal(2, r.ErrorCode);
        Assert.Equal("bad zip", r.ErrorMessage);
        Assert.Empty(r.Diagnostics);
        Assert.Equal(0, r.PageCount);
    }

    [Fact]
    public void BufferResult_HasDataSize()
    {
        var r = Parse("{\"type\":\"buffer_result\",\"id\":2,\"success\":true,\"page_count\":1,\"diagnostics\":[],\"data_size\":1234}");
        Assert.Equal("buffer_result", r.Type);
        Assert.Equal(1234, r.DataSize);
    }

    [Fact]
    public void Error()
    {
        var r = Parse("{\"type\":\"error\",\"message\":\"unknown message type\"}");
        Assert.Equal("error", r.Type);
        Assert.Equal("unknown message type", r.Message);
    }

    [Fact]
    public void Diagnostics_UnknownValues_AreTolerated()
    {
        var r = Parse("{\"type\":\"result\",\"id\":1,\"success\":true,\"diagnostics\":[{\"severity\":\"debug\",\"category\":\"layout\",\"message\":\"m\",\"extra\":1}, 5, {\"severity\":\"INFO\"}]}");
        Assert.Equal(2, r.Diagnostics.Count);
        Assert.Equal(DiagnosticSeverity.Info, r.Diagnostics[0].Severity);
        Assert.Equal(DiagnosticCategory.General, r.Diagnostics[0].Category);
        Assert.Equal("", r.Diagnostics[1].Message);
        Assert.Null(r.Diagnostics[1].FontName);
    }

    [Fact]
    public void UnknownFields_AreIgnored()
    {
        var r = Parse("{\"type\":\"result\",\"id\":1,\"success\":true,\"future\":{\"a\":[1,2]},\"page_count\":2}");
        Assert.Equal(2, r.PageCount);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"id\":1}")]
    [InlineData("{\"type\":5}")]
    [InlineData("")]
    public void Malformed_ThrowsProtocolException(string json)
    {
        Assert.Throws<WorkerProtocolException>(() => Parse(json));
    }
}

public class StderrTailTests
{
    [Fact]
    public void KeepsEverything_UnderCapacity()
    {
        var tail = new StderrTail(64);
        var b = Encoding.ASCII.GetBytes("hello\n");
        tail.Append(b, 0, b.Length);
        Assert.Equal("hello", tail.ToString());
        Assert.Equal(6, tail.TotalBytes);
    }

    [Fact]
    public void KeepsOnlyTheLastBytes_WhenWrapping()
    {
        var tail = new StderrTail(10);
        for (int i = 0; i < 25; i++)
        {
            var b = new[] { (byte)('a' + i) };
            tail.Append(b, 0, 1);
        }
        Assert.Equal("…pqrstuvwxy", tail.ToString());
        Assert.Equal(25, tail.TotalBytes);
    }

    [Fact]
    public void LargeAppend_KeepsItsEnd()
    {
        var tail = new StderrTail(8);
        var b = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        tail.Append(b, 0, b.Length);
        Assert.Equal("…89ABCDEF", tail.ToString());
        var c = Encoding.ASCII.GetBytes("XY");
        tail.Append(c, 0, 2);
        Assert.Equal("…ABCDEFXY", tail.ToString());
    }

    [Fact]
    public async Task Drain_IsBounded()
    {
        var tail = new StderrTail(1024);
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("line of stderr output\n", 50_000)) + "THE-END");
        await tail.DrainAsync(new MemoryStream(data));
        Assert.Equal(data.Length, tail.TotalBytes);
        var text = tail.ToString();
        Assert.True(text.Length <= 1025);
        Assert.EndsWith("THE-END", text);
    }
}
