package com.wordcraftpdf.testing;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.DataInputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;

/**
 * A stand-in for {@code wcpdf_worker} that speaks docs/protocol.md, for deterministic pool tests.
 *
 * <p>Start-up behaviour comes from the {@code FAKE_MODE} environment variable:
 * {@code normal}, {@code init-error} (answers {@code error} to init), {@code exit-on-start}
 * (writes to stderr and exits with status 7 before answering).</p>
 *
 * <p>Per-conversion behaviour comes from the {@code title} option:
 * {@code crash} (dies with status 134), {@code hang}, {@code slow:<ms>}, {@code fail}
 * (load failure, code 2), {@code garbage} (a non-JSON frame), {@code wrong-id},
 * {@code stderr-flood:<bytes>}, {@code spawn-child-and-hang} (starts {@code sleep 600}, then hangs).</p>
 *
 * <p>A success carries {@code page_count} = this worker's conversion count and diagnostics
 * {@code pid=<pid>}, {@code format=<n>} and {@code options=<json>}. The "PDF" is
 * {@code %PDF-FAKE\n} followed by the document bytes.</p>
 */
public final class FakeWorker {

    static final byte[] PDF_PREFIX = "%PDF-FAKE\n".getBytes(StandardCharsets.US_ASCII);

    private FakeWorker() {}

    public static void main(String[] args) throws Exception {
        String mode = System.getenv().getOrDefault("FAKE_MODE", "normal");
        DataInputStream in = new DataInputStream(new BufferedInputStream(System.in, 1 << 16));
        OutputStream out = new BufferedOutputStream(System.out, 1 << 16);

        if ("exit-on-start".equals(mode)) {
            System.err.println("fake: cannot load libwordcraft.so: no such file");
            System.err.flush();
            System.exit(7);
        }

        int conversions = 0;
        while (true) {
            byte[] frame = readFrame(in);
            if (frame == null) {
                System.exit(0);
            }
            JsonObject req = JsonParser.parseString(new String(frame, StandardCharsets.UTF_8)).getAsJsonObject();
            String type = req.get("type").getAsString();
            switch (type) {
                case "init": {
                    JsonObject r = new JsonObject();
                    if ("init-error".equals(mode)) {
                        r.addProperty("type", "error");
                        r.addProperty("message", "fake init failure");
                    } else {
                        r.addProperty("type", "ready");
                        r.addProperty("version", "FakeWorker 1.0");
                        JsonArray diags = new JsonArray();
                        if (req.has("font_paths")) {
                            for (com.google.gson.JsonElement p : req.getAsJsonArray("font_paths")) {
                                diags.add(diag("info", "font", "font folder " + p.getAsString()));
                            }
                        }
                        r.add("diagnostics", diags);
                    }
                    send(out, r);
                    break;
                }
                case "quit":
                    System.exit(0);
                    break;
                case "convert":
                case "convert_buffer": {
                    boolean buffer = "convert_buffer".equals(type);
                    byte[] doc = buffer ? readFrame(in) : null;
                    if (buffer && doc == null) System.exit(0);
                    long id = req.get("id").getAsLong();
                    JsonObject options = req.has("options") ? req.getAsJsonObject("options") : new JsonObject();
                    String command = options.has("title") ? options.get("title").getAsString() : "";

                    if (command.equals("crash")) {
                        System.err.println("fake: simulated crash");
                        System.err.flush();
                        Runtime.getRuntime().halt(134);
                    }
                    if (command.equals("hang")) {
                        Thread.sleep(Long.MAX_VALUE);
                    }
                    if (command.equals("spawn-child-and-hang")) {
                        new ProcessBuilder("sleep", "600").start();
                        Thread.sleep(Long.MAX_VALUE);
                    }
                    if (command.startsWith("slow:")) {
                        Thread.sleep(Long.parseLong(command.substring(5)));
                    }
                    if (command.startsWith("stderr-flood:")) {
                        long n = Long.parseLong(command.substring(13));
                        byte[] line = "fake stderr noise ................................................\n"
                                .getBytes(StandardCharsets.US_ASCII);
                        for (long written = 0; written < n; written += line.length) {
                            System.err.write(line);
                        }
                        System.err.flush();
                    }
                    if (command.equals("garbage")) {
                        writeFrame(out, "this is not json".getBytes(StandardCharsets.US_ASCII));
                        out.flush();
                        break;
                    }

                    conversions++;
                    JsonObject r = new JsonObject();
                    r.addProperty("type", buffer ? "buffer_result" : "result");
                    r.addProperty("id", command.equals("wrong-id") ? id + 1000 : id);
                    if (command.equals("fail")) {
                        r.addProperty("success", false);
                        r.addProperty("error_code", 2);
                        r.addProperty("error_message", "simulated load failure");
                        r.add("diagnostics", new JsonArray());
                        send(out, r);
                        break;
                    }
                    byte[] content = buffer ? doc : Files.readAllBytes(Paths.get(req.get("input").getAsString()));
                    byte[] pdf = new byte[PDF_PREFIX.length + content.length];
                    System.arraycopy(PDF_PREFIX, 0, pdf, 0, PDF_PREFIX.length);
                    System.arraycopy(content, 0, pdf, PDF_PREFIX.length, content.length);

                    r.addProperty("success", true);
                    r.add("error_code", null);
                    r.add("error_message", null);
                    r.addProperty("page_count", conversions);
                    JsonArray diags = new JsonArray();
                    diags.add(diag("info", "general", "pid=" + ProcessHandle.current().pid()));
                    diags.add(diag("info", "general", "format=" + req.get("format").getAsInt()));
                    diags.add(diag("info", "general", "options=" + options));
                    r.add("diagnostics", diags);
                    if (buffer) {
                        r.addProperty("data_size", pdf.length);
                        send(out, r);
                        writeFrame(out, pdf);
                        out.flush();
                    } else {
                        Files.write(Paths.get(req.get("output").getAsString()), pdf);
                        send(out, r);
                    }
                    break;
                }
                default: {
                    JsonObject r = new JsonObject();
                    r.addProperty("type", "error");
                    r.addProperty("message", "unknown message type " + type);
                    send(out, r);
                }
            }
        }
    }

    private static JsonObject diag(String severity, String category, String message) {
        JsonObject d = new JsonObject();
        d.addProperty("severity", severity);
        d.addProperty("category", category);
        d.addProperty("message", message);
        return d;
    }

    private static void send(OutputStream out, JsonObject o) throws IOException {
        writeFrame(out, o.toString().getBytes(StandardCharsets.UTF_8));
        out.flush();
    }

    private static void writeFrame(OutputStream out, byte[] payload) throws IOException {
        int n = payload.length;
        out.write(new byte[] {(byte) n, (byte) (n >>> 8), (byte) (n >>> 16), (byte) (n >>> 24)});
        out.write(payload);
    }

    private static byte[] readFrame(DataInputStream in) throws IOException {
        byte[] len = new byte[4];
        try {
            in.readFully(len);
        } catch (EOFException e) {
            return null;
        }
        int n = (len[0] & 0xFF) | (len[1] & 0xFF) << 8 | (len[2] & 0xFF) << 16 | (len[3] & 0xFF) << 24;
        byte[] payload = new byte[n];
        try {
            in.readFully(payload);
        } catch (EOFException e) {
            return null;
        }
        return payload;
    }
}
