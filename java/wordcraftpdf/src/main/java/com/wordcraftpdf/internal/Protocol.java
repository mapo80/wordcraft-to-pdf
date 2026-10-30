package com.wordcraftpdf.internal;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParseException;
import com.google.gson.JsonParser;
import com.google.gson.JsonPrimitive;
import com.wordcraftpdf.ConversionDiagnostic;
import com.wordcraftpdf.ConversionOptions;
import com.wordcraftpdf.DiagnosticCategory;
import com.wordcraftpdf.DiagnosticSeverity;
import com.wordcraftpdf.WordCraftPdfErrorCode;

import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * Wire protocol of {@code wcpdf_worker} (docs/protocol.md): frames of a 4-byte little-endian
 * length followed by the payload; JSON frames are UTF-8.
 */
public final class Protocol {

    /** Largest frame either side accepts: 512 MiB. */
    public static final int MAX_FRAME = 512 * 1024 * 1024;

    private static final Gson GSON = new GsonBuilder().disableHtmlEscaping().serializeNulls().create();

    private Protocol() {}

    // ------------------------------------------------------------------------------------------
    // Framing

    /** Write one frame (no flush). */
    public static void writeFrame(OutputStream out, byte[] payload) throws IOException {
        writeFrame(out, payload, 0, payload.length);
    }

    /** Write one frame from {@code payload[offset, offset + length)} (no flush, no copy). */
    public static void writeFrame(OutputStream out, byte[] payload, int offset, int length) throws IOException {
        if (length < 0 || length > MAX_FRAME) {
            throw new IOException("Frame of " + length + " bytes exceeds the " + MAX_FRAME + "-byte limit");
        }
        out.write(new byte[] {
                (byte) length, (byte) (length >>> 8), (byte) (length >>> 16), (byte) (length >>> 24)});
        out.write(payload, offset, length);
    }

    /** Serialize {@code message} and write it as one frame (no flush). */
    public static void writeJsonFrame(OutputStream out, JsonObject message) throws IOException {
        writeFrame(out, toJsonBytes(message));
    }

    /**
     * Read one frame into an exactly-sized array.
     *
     * @return the payload, or null if the stream ended (cleanly or mid-frame): the peer is gone.
     * @throws ProtocolException if the length prefix exceeds {@link #MAX_FRAME}.
     */
    public static byte[] readFrame(InputStream in) throws IOException {
        int length = readLength(in);
        if (length < 0) {
            return null;
        }
        return readPayload(in, length);
    }

    /**
     * Read one frame that must be exactly {@code expectedLength} bytes long (the PDF frame that
     * follows a {@code buffer_result}).
     *
     * @return the payload, or null if the stream ended.
     * @throws ProtocolException if the frame has another length.
     */
    public static byte[] readFrame(InputStream in, int expectedLength) throws IOException {
        int length = readLength(in);
        if (length < 0) {
            return null;
        }
        if (length != expectedLength) {
            throw new ProtocolException("Data frame of " + length + " bytes, expected " + expectedLength);
        }
        return readPayload(in, length);
    }

    /** The frame length, or -1 at end of stream. */
    private static int readLength(InputStream in) throws IOException {
        int b0 = in.read();
        if (b0 < 0) return -1;
        int b1 = in.read();
        int b2 = in.read();
        int b3 = in.read();
        if ((b1 | b2 | b3) < 0) return -1;
        long length = (b0 | (b1 << 8) | (b2 << 16) | ((long) b3 << 24)) & 0xFFFFFFFFL;
        if (length > MAX_FRAME) {
            throw new ProtocolException("Frame of " + length + " bytes exceeds the " + MAX_FRAME + "-byte limit");
        }
        return (int) length;
    }

    private static byte[] readPayload(InputStream in, int length) throws IOException {
        byte[] payload = new byte[length];
        try {
            readFully(in, payload);
        } catch (EOFException e) {
            return null;
        }
        return payload;
    }

    private static void readFully(InputStream in, byte[] buffer) throws IOException {
        int done = 0;
        while (done < buffer.length) {
            int n = in.read(buffer, done, buffer.length - done);
            if (n < 0) {
                throw new EOFException();
            }
            done += n;
        }
    }

    // ------------------------------------------------------------------------------------------
    // JSON codec

    /** Serialize a JSON object to UTF-8 bytes. */
    public static byte[] toJsonBytes(JsonObject message) {
        return GSON.toJson(message).getBytes(StandardCharsets.UTF_8);
    }

    /**
     * Parse a UTF-8 JSON object.
     *
     * @throws ProtocolException if the payload is not a JSON object.
     */
    public static JsonObject parseObject(byte[] payload) throws ProtocolException {
        try {
            JsonElement element = JsonParser.parseString(new String(payload, StandardCharsets.UTF_8));
            if (!element.isJsonObject()) {
                throw new ProtocolException("Expected a JSON object from the worker");
            }
            return element.getAsJsonObject();
        } catch (JsonParseException e) {
            throw new ProtocolException("Malformed JSON from the worker: " + e.getMessage());
        }
    }

    // ------------------------------------------------------------------------------------------
    // Requests

    public static JsonObject initRequest(List<String> fontPaths) {
        JsonObject o = new JsonObject();
        o.addProperty("type", "init");
        if (fontPaths != null && !fontPaths.isEmpty()) {
            JsonArray paths = new JsonArray(fontPaths.size());
            for (String p : fontPaths) {
                paths.add(p);
            }
            o.add("font_paths", paths);
        }
        return o;
    }

    public static JsonObject convertRequest(long id, String input, String output, int format, ConversionOptions options) {
        JsonObject o = new JsonObject();
        o.addProperty("type", "convert");
        o.addProperty("id", id);
        o.addProperty("input", input);
        o.addProperty("output", output);
        o.addProperty("format", format);
        addOptions(o, options);
        return o;
    }

    public static JsonObject convertBufferRequest(long id, int format, int dataSize, ConversionOptions options) {
        JsonObject o = new JsonObject();
        o.addProperty("type", "convert_buffer");
        o.addProperty("id", id);
        o.addProperty("format", format);
        o.addProperty("data_size", dataSize);
        addOptions(o, options);
        return o;
    }

    public static JsonObject quitRequest() {
        JsonObject o = new JsonObject();
        o.addProperty("type", "quit");
        return o;
    }

    /** The protocol's {@code options} object; only fields that differ from the defaults are sent. */
    public static JsonObject optionsJson(ConversionOptions options) {
        JsonObject o = new JsonObject();
        if (options == null) {
            return o;
        }
        if (options.getPageRange() != null) o.addProperty("page_range", options.getPageRange());
        if (options.getPassword() != null) o.addProperty("password", options.getPassword());
        if (options.isTaggedPdf()) o.addProperty("tagged_pdf", true);
        if (options.isUpdateFields()) o.addProperty("update_fields", true);
        if (options.isIncludeMarkup()) o.addProperty("include_markup", true);
        if (options.isIncludeComments()) o.addProperty("include_comments", true);
        if (options.getTitle() != null) o.addProperty("title", options.getTitle());
        if (options.getAuthor() != null) o.addProperty("author", options.getAuthor());
        return o;
    }

    private static void addOptions(JsonObject request, ConversionOptions options) {
        JsonObject opts = optionsJson(options);
        if (opts.size() > 0) {
            request.add("options", opts);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Responses

    /** The JSON part of a {@code result} / {@code buffer_result} response. */
    public static final class ResultHeader {
        public final boolean success;
        public final WordCraftPdfErrorCode errorCode;
        public final String errorMessage;
        public final int pageCount;
        /** PDF size announced by a successful {@code buffer_result}; -1 otherwise. */
        public final int dataSize;
        public final List<ConversionDiagnostic> diagnostics;

        ResultHeader(boolean success, WordCraftPdfErrorCode errorCode, String errorMessage,
                     int pageCount, int dataSize, List<ConversionDiagnostic> diagnostics) {
            this.success = success;
            this.errorCode = errorCode;
            this.errorMessage = errorMessage;
            this.pageCount = pageCount;
            this.dataSize = dataSize;
            this.diagnostics = diagnostics;
        }
    }

    /**
     * Validate and decode the answer to a conversion request.
     *
     * @param expectedType {@code "result"} or {@code "buffer_result"}.
     * @param expectedId   the request's id.
     * @throws ProtocolException if the response is not the answer to that request (an {@code error}
     *                           message, another type or id, a missing field): the stream can no
     *                           longer be trusted.
     */
    public static ResultHeader parseResult(JsonObject root, String expectedType, long expectedId)
            throws ProtocolException {
        String type = getString(root, "type");
        if ("error".equals(type)) {
            String message = getString(root, "message");
            throw new ProtocolException("Worker error: " + (message != null ? message : "unspecified"));
        }
        if (!expectedType.equals(type)) {
            throw new ProtocolException("Unexpected response type '" + type + "', expected '" + expectedType + "'");
        }
        JsonPrimitive id = getPrimitive(root, "id");
        if (id == null || !id.isNumber() || id.getAsLong() != expectedId) {
            throw new ProtocolException("Response id " + id + " does not match request id " + expectedId);
        }
        JsonPrimitive success = getPrimitive(root, "success");
        if (success == null || !success.isBoolean()) {
            throw new ProtocolException("Response has no 'success' flag");
        }
        List<ConversionDiagnostic> diagnostics = parseDiagnostics(root.get("diagnostics"));
        if (success.getAsBoolean()) {
            int pageCount = getInt(root, "page_count", 0);
            int dataSize = -1;
            if ("buffer_result".equals(expectedType)) {
                long size = getLong(root, "data_size", -1);
                if (size < 0 || size > MAX_FRAME) {
                    throw new ProtocolException("Invalid 'data_size' in buffer_result: " + size);
                }
                dataSize = (int) size;
            }
            return new ResultHeader(true, null, null, pageCount, dataSize, diagnostics);
        }
        int code = getInt(root, "error_code", WordCraftPdfErrorCode.UNKNOWN.getValue());
        String message = getString(root, "error_message");
        return new ResultHeader(false, WordCraftPdfErrorCode.fromValue(code),
                message != null ? message : "Conversion failed", 0, -1, diagnostics);
    }

    /** Decode a {@code diagnostics} array; null or anything but an array yields an empty list. */
    public static List<ConversionDiagnostic> parseDiagnostics(JsonElement element) {
        if (element == null || !element.isJsonArray() || element.getAsJsonArray().size() == 0) {
            return Collections.emptyList();
        }
        JsonArray array = element.getAsJsonArray();
        List<ConversionDiagnostic> result = new ArrayList<ConversionDiagnostic>(array.size());
        for (JsonElement e : array) {
            if (!e.isJsonObject()) continue;
            JsonObject o = e.getAsJsonObject();
            result.add(new ConversionDiagnostic(
                    DiagnosticSeverity.fromString(getString(o, "severity")),
                    DiagnosticCategory.fromString(getString(o, "category")),
                    getString(o, "message"),
                    getString(o, "font"),
                    getString(o, "substituted_with")));
        }
        return result;
    }

    // ------------------------------------------------------------------------------------------
    // Field access tolerant of missing / null / mistyped values

    static JsonPrimitive getPrimitive(JsonObject o, String key) {
        JsonElement e = o.get(key);
        return e != null && e.isJsonPrimitive() ? e.getAsJsonPrimitive() : null;
    }

    public static String getString(JsonObject o, String key) {
        JsonPrimitive p = getPrimitive(o, key);
        return p != null && p.isString() ? p.getAsString() : null;
    }

    static long getLong(JsonObject o, String key, long fallback) {
        JsonPrimitive p = getPrimitive(o, key);
        if (p == null || !p.isNumber()) return fallback;
        try {
            return p.getAsLong();
        } catch (NumberFormatException e) {
            return fallback;
        }
    }

    static int getInt(JsonObject o, String key, int fallback) {
        long v = getLong(o, key, fallback);
        return v >= Integer.MIN_VALUE && v <= Integer.MAX_VALUE ? (int) v : fallback;
    }

    /** The worker sent something the protocol doesn't allow; its stream can't be trusted. */
    public static final class ProtocolException extends IOException {
        private static final long serialVersionUID = 1L;

        public ProtocolException(String message) {
            super(message);
        }
    }
}
