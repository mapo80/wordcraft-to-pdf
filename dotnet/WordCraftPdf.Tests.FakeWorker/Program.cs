// Fake wcpdf_worker for the SDK tests. Speaks the worker protocol (docs/protocol.md) and
// behaves according to commands:
//
// init: font_paths entries starting with "fake:" are directives
//   fake:init-error       answer {"type":"error"}
//   fake:init-crash       write ~200 KB to stderr (ending with FAKE-STDERR-END), exit(3) without answering
//   fake:init-hang        never answer
//   fake:init-delay=<ms>  sleep before answering
//   fake:ignore-quit      on quit / end of stdin, hang instead of exiting
//   fake:bad-font-dir     report a startup diagnostic
//
// conversions: the document (buffer mode) or the input file's content (file mode), as text
//   CRASH                 exit(134) without answering
//   CRASH-PARTIAL         write half a response frame, then exit
//   HANG                  never answer
//   SLEEP:<ms>            sleep, then succeed
//   FAIL:<code>           answer success=false with error_code <code>
//   ERROR                 answer {"type":"error"} instead of a result
//   BADID                 answer with the wrong id
//   DIAG                  succeed with a font diagnostic
//   ECHO-REQUEST          succeed; the PDF is the request JSON
//   anything else         succeed; the PDF is "%PDF-FAKE pid=<pid>\n" + the document
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var pid = Environment.ProcessId;
var initialized = false;
var ignoreQuit = false;

while (true)
{
    var frame = ReadFrame(stdin);
    if (frame is null)
        break;
    JsonNode? req;
    try
    {
        req = JsonNode.Parse(frame);
    }
    catch (JsonException)
    {
        Send(new JsonObject { ["type"] = "error", ["message"] = "invalid message" });
        continue;
    }

    switch ((string?)req?["type"])
    {
        case "init":
        {
            // The SDK sends absolute paths: a directive is whatever follows "fake:".
            var dirs = req!["font_paths"]?.AsArray()
                .Select(n => (string?)n ?? "")
                .Select(p => p.IndexOf("fake:", StringComparison.Ordinal) is var i and >= 0 ? p[i..] : p)
                .ToList() ?? new List<string>();
            foreach (var d in dirs)
            {
                if (d == "fake:init-hang")
                    Thread.Sleep(Timeout.Infinite);
                if (d.StartsWith("fake:init-delay=", StringComparison.Ordinal))
                    Thread.Sleep(int.Parse(d["fake:init-delay=".Length..]));
                if (d == "fake:ignore-quit")
                    ignoreQuit = true;
                if (d == "fake:init-crash")
                {
                    var err = Console.OpenStandardError();
                    var line = Encoding.UTF8.GetBytes(new string('x', 1023) + "\n");
                    for (int i = 0; i < 200; i++)
                        err.Write(line);
                    err.Write(Encoding.UTF8.GetBytes("FAKE-STDERR-END\n"));
                    err.Flush();
                    Environment.Exit(3);
                }
                if (d == "fake:init-error")
                {
                    Send(new JsonObject { ["type"] = "error", ["message"] = "fake init failure" });
                    goto nextFrame;
                }
            }
            var diags = new JsonArray();
            if (dirs.Contains("fake:bad-font-dir"))
                diags.Add(new JsonObject { ["severity"] = "warning", ["category"] = "general", ["message"] = "font folder not found: fake" });
            initialized = true;
            Send(new JsonObject { ["type"] = "ready", ["version"] = $"FakeWorker 1.0 (pid {pid})", ["diagnostics"] = diags });
            break;
        }
        case "convert":
        {
            var id = (long)req!["id"]!;
            var input = (string)req["input"]!;
            var output = (string)req["output"]!;
            if (!initialized)
            {
                SendResult("result", id, false, 9, "not initialised");
                break;
            }
            if (!File.Exists(input))
            {
                SendResult("result", id, false, 5, $"{input}: not found");
                break;
            }
            var doc = File.ReadAllBytes(input);
            var pdf = Handle("result", id, doc, frame);
            if (pdf is not null)
            {
                File.WriteAllBytes(output, pdf);
                SendResult("result", id, true, null, null);
            }
            break;
        }
        case "convert_buffer":
        {
            var id = (long)req!["id"]!;
            var doc = ReadFrame(stdin);
            if (doc is null)
                return 0;
            if (!initialized)
            {
                SendResult("buffer_result", id, false, 9, "not initialised");
                break;
            }
            var size = (long?)req["data_size"];
            if (size is not null && size != doc.Length)
            {
                SendResult("buffer_result", id, false, 10, "data frame size mismatch");
                break;
            }
            var pdf = Handle("buffer_result", id, doc, frame);
            if (pdf is not null)
            {
                SendResult("buffer_result", id, true, null, null, pdf.Length);
                WriteFrame(pdf);
            }
            break;
        }
        case "quit":
            goto quit;
        default:
            Send(new JsonObject { ["type"] = "error", ["message"] = "unknown message type" });
            break;
    }
    nextFrame:;
}

quit:
if (ignoreQuit)
    Thread.Sleep(Timeout.Infinite);
return 0;

// Returns the PDF to send, or null when the answer was already sent.
byte[]? Handle(string type, long id, byte[] doc, byte[] request)
{
    var text = Encoding.UTF8.GetString(doc).Trim();
    if (text == "CRASH")
        Environment.Exit(134);
    if (text == "CRASH-PARTIAL")
    {
        stdout.Write(new byte[] { 200, 0 });
        stdout.Flush();
        Environment.Exit(134);
    }
    if (text == "HANG")
        Thread.Sleep(Timeout.Infinite);
    if (text.StartsWith("SLEEP:", StringComparison.Ordinal))
        Thread.Sleep(int.Parse(text["SLEEP:".Length..]));
    if (text.StartsWith("FAIL:", StringComparison.Ordinal))
    {
        SendResult(type, id, false, int.Parse(text["FAIL:".Length..]), "fake failure");
        return null;
    }
    if (text == "ERROR")
    {
        Send(new JsonObject { ["type"] = "error", ["message"] = "fake protocol error" });
        return null;
    }
    if (text == "BADID")
    {
        SendResult(type, id + 1000, false, 2, "wrong id");
        return null;
    }
    if (text == "DIAG")
        diagnosticsForNext = true;
    if (text == "ECHO-REQUEST")
        return request;
    var prefix = Encoding.ASCII.GetBytes($"%PDF-FAKE pid={pid}\n");
    var pdf = new byte[prefix.Length + doc.Length];
    prefix.CopyTo(pdf, 0);
    doc.CopyTo(pdf, prefix.Length);
    return pdf;
}

void SendResult(string type, long id, bool success, int? code, string? message, long? dataSize = null)
{
    var diags = new JsonArray();
    if (diagnosticsForNext)
    {
        diags.Add(new JsonObject
        {
            ["severity"] = "warning", ["category"] = "font",
            ["message"] = "font \"Calibri\" is not available; substituted with \"Carlito\"",
            ["font"] = "Calibri", ["substituted_with"] = "Carlito"
        });
        diagnosticsForNext = false;
    }
    var o = new JsonObject
    {
        ["type"] = type, ["id"] = id, ["success"] = success,
        ["error_code"] = code, ["error_message"] = message, ["diagnostics"] = diags
    };
    if (success)
        o["page_count"] = 3;
    if (dataSize is not null)
        o["data_size"] = dataSize;
    Send(o);
}

void Send(JsonObject o) => WriteFrame(Encoding.UTF8.GetBytes(o.ToJsonString()));

void WriteFrame(byte[] payload)
{
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
    stdout.Write(header);
    stdout.Write(payload);
    stdout.Flush();
}

static byte[]? ReadFrame(Stream s)
{
    var header = new byte[4];
    if (!ReadExact(s, header))
        return null;
    var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
    var buf = new byte[len];
    return ReadExact(s, buf) ? buf : null;
}

static bool ReadExact(Stream s, byte[] buf)
{
    int total = 0;
    while (total < buf.Length)
    {
        int n = s.Read(buf, total, buf.Length - total);
        if (n == 0)
            return false;
        total += n;
    }
    return true;
}

partial class Program
{
    static bool diagnosticsForNext;
}
