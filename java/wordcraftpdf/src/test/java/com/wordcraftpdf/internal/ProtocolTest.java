package com.wordcraftpdf.internal;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.wordcraftpdf.ConversionDiagnostic;
import com.wordcraftpdf.ConversionOptions;
import com.wordcraftpdf.DiagnosticCategory;
import com.wordcraftpdf.DiagnosticSeverity;
import com.wordcraftpdf.WordCraftPdfErrorCode;
import com.wordcraftpdf.internal.Protocol.ProtocolException;
import com.wordcraftpdf.internal.Protocol.ResultHeader;
import org.junit.jupiter.api.Test;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.List;

import static org.junit.jupiter.api.Assertions.*;

class ProtocolTest {

    // ---- Framing ----

    @Test
    void frame_roundTrip() throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] payload = "hello world".getBytes(StandardCharsets.UTF_8);
        Protocol.writeFrame(out, payload);
        assertArrayEquals(payload, Protocol.readFrame(new ByteArrayInputStream(out.toByteArray())));
    }

    @Test
    void frame_lengthIsLittleEndian() throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        Protocol.writeFrame(out, new byte[0x0102]);
        byte[] raw = out.toByteArray();
        assertEquals(4 + 0x0102, raw.length);
        assertEquals(0x02, raw[0]);
        assertEquals(0x01, raw[1]);
        assertEquals(0, raw[2]);
        assertEquals(0, raw[3]);
        assertEquals(0x0102, ByteBuffer.wrap(raw, 0, 4).order(ByteOrder.LITTLE_ENDIAN).getInt());
    }

    @Test
    void frame_writesSliceWithoutCopy() throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buffer = {9, 9, 1, 2, 3, 9};
        Protocol.writeFrame(out, buffer, 2, 3);
        assertArrayEquals(new byte[] {3, 0, 0, 0, 1, 2, 3}, out.toByteArray());
    }

    @Test
    void frame_readsLengthAbove2GiBAsUnsignedAndRejectsIt() {
        byte[] raw = {(byte) 0xFF, (byte) 0xFF, (byte) 0xFF, (byte) 0xFF};
        ProtocolException e = assertThrows(ProtocolException.class,
                () -> Protocol.readFrame(new ByteArrayInputStream(raw)));
        assertTrue(e.getMessage().contains("4294967295"), e.getMessage());
    }

    @Test
    void frame_rejectsLengthJustAboveLimit() {
        byte[] raw = new byte[4];
        ByteBuffer.wrap(raw).order(ByteOrder.LITTLE_ENDIAN).putInt(Protocol.MAX_FRAME + 1);
        assertThrows(ProtocolException.class, () -> Protocol.readFrame(new ByteArrayInputStream(raw)));
    }

    @Test
    void frame_writeRejectsOversizedLength() {
        assertThrows(IOException.class,
                () -> Protocol.writeFrame(new ByteArrayOutputStream(), new byte[1], 0, Protocol.MAX_FRAME + 1));
    }

    @Test
    void frame_nullOnEmptyStream() throws IOException {
        assertNull(Protocol.readFrame(new ByteArrayInputStream(new byte[0])));
    }

    @Test
    void frame_nullOnPartialHeader() throws IOException {
        assertNull(Protocol.readFrame(new ByteArrayInputStream(new byte[] {1, 2})));
    }

    @Test
    void frame_nullOnTruncatedPayload() throws IOException {
        byte[] data = new byte[7];
        ByteBuffer.wrap(data, 0, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(10);
        assertNull(Protocol.readFrame(new ByteArrayInputStream(data)));
    }

    @Test
    void frame_emptyPayload() throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        Protocol.writeFrame(out, new byte[0]);
        byte[] read = Protocol.readFrame(new ByteArrayInputStream(out.toByteArray()));
        assertNotNull(read);
        assertEquals(0, read.length);
    }

    @Test
    void frame_sequenceAndEof() throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        for (String s : new String[] {"first", "second", "third"}) {
            Protocol.writeFrame(out, s.getBytes(StandardCharsets.UTF_8));
        }
        ByteArrayInputStream in = new ByteArrayInputStream(out.toByteArray());
        assertEquals("first", new String(Protocol.readFrame(in), StandardCharsets.UTF_8));
        assertEquals("second", new String(Protocol.readFrame(in), StandardCharsets.UTF_8));
        assertEquals("third", new String(Protocol.readFrame(in), StandardCharsets.UTF_8));
        assertNull(Protocol.readFrame(in));
    }

    @Test
    void frame_largePayloadReadExactly() throws IOException {
        byte[] payload = new byte[3 * 1024 * 1024 + 7];
        for (int i = 0; i < payload.length; i++) payload[i] = (byte) (i * 31);
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        Protocol.writeFrame(out, payload);
        byte[] read = Protocol.readFrame(new ByteArrayInputStream(out.toByteArray()), payload.length);
        assertEquals(payload.length, read.length);
        assertArrayEquals(payload, read);
    }

    @Test
    void frame_expectedLengthMismatchIsProtocolError() throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        Protocol.writeFrame(out, new byte[5]);
        assertThrows(ProtocolException.class,
                () -> Protocol.readFrame(new ByteArrayInputStream(out.toByteArray()), 6));
    }

    // ---- JSON codec ----

    @Test
    void json_escapesSpecialCharactersAndRoundTrips() throws ProtocolException {
        String tricky = "quote\" backslash\\ slash/ newline\n tab\t cr\r nul\u0000 bell\u0007 "
                + "<html>&amp;' e\u0301 caf\u00e9 \u4e2d\u6587 emoji\uD83D\uDE00 ls\u2028 ps\u2029";
        JsonObject o = Protocol.convertRequest(1, tricky, "/out.pdf", 1,
                ConversionOptions.builder().title(tricky).author("Ren\u00e9e").build());
        byte[] bytes = Protocol.toJsonBytes(o);
        String text = new String(bytes, StandardCharsets.UTF_8);

        // Control characters never appear raw inside the JSON text.
        for (char c : text.toCharArray()) {
            assertTrue(c >= 0x20, "raw control character U+" + Integer.toHexString(c) + " in " + text);
        }
        assertTrue(text.contains("\\\""));
        assertTrue(text.contains("\\\\"));
        assertTrue(text.contains("\\n"));
        assertTrue(text.contains("\\u0000"));

        JsonObject back = Protocol.parseObject(bytes);
        assertEquals(tricky, back.get("input").getAsString());
        assertEquals(tricky, back.getAsJsonObject("options").get("title").getAsString());
        assertEquals("Ren\u00e9e", back.getAsJsonObject("options").get("author").getAsString());
    }

    @Test
    void json_nonAsciiIsUtf8Encoded() {
        JsonObject o = new JsonObject();
        o.addProperty("s", "\u00e9\uD83D\uDE00");
        byte[] bytes = Protocol.toJsonBytes(o);
        String expected = "{\"s\":\"\u00e9\uD83D\uDE00\"}";
        assertArrayEquals(expected.getBytes(StandardCharsets.UTF_8), bytes);
    }

    @Test
    void json_parsesUnicodeEscapesIncludingSurrogatePairs() throws ProtocolException {
        String json = "{\"type\":\"result\",\"id\":3,\"success\":true,\"page_count\":2,\"diagnostics\":["
                + "{\"severity\":\"warning\",\"category\":\"font\","
                + "\"message\":\"font \\\"Caf\\u00e9 \\ud83d\\ude00\\\" is not available; substituted with \\\"Carlito\\\"\","
                + "\"font\":\"Caf\\u00e9 \\ud83d\\ude00\",\"substituted_with\":\"Carlito\"}]}";
        ResultHeader h = Protocol.parseResult(Protocol.parseObject(json.getBytes(StandardCharsets.UTF_8)), "result", 3);
        assertTrue(h.success);
        assertEquals(2, h.pageCount);
        assertEquals(1, h.diagnostics.size());
        ConversionDiagnostic d = h.diagnostics.get(0);
        assertEquals(DiagnosticSeverity.WARNING, d.getSeverity());
        assertEquals(DiagnosticCategory.FONT, d.getCategory());
        assertEquals("Caf\u00e9 \uD83D\uDE00", d.getFontName());
        assertEquals("Carlito", d.getSubstitutedWith());
        assertEquals("font \"Caf\u00e9 \uD83D\uDE00\" is not available; substituted with \"Carlito\"", d.getMessage());
    }

    @Test
    void json_parsesRawUtf8() throws ProtocolException {
        byte[] bytes = "{\"type\":\"error\",\"message\":\"\u4e2d\u6587 \uD83D\uDE00\"}".getBytes(StandardCharsets.UTF_8);
        assertEquals("\u4e2d\u6587 \uD83D\uDE00", Protocol.getString(Protocol.parseObject(bytes), "message"));
    }

    @Test
    void json_malformedIsProtocolError() {
        assertThrows(ProtocolException.class, () -> Protocol.parseObject("{\"type\":".getBytes(StandardCharsets.UTF_8)));
        assertThrows(ProtocolException.class, () -> Protocol.parseObject("not json at all".getBytes(StandardCharsets.UTF_8)));
        assertThrows(ProtocolException.class, () -> Protocol.parseObject("[1,2]".getBytes(StandardCharsets.UTF_8)));
        assertThrows(ProtocolException.class, () -> Protocol.parseObject(new byte[0]));
    }

    // ---- Requests ----

    @Test
    void initRequest_withAndWithoutFontPaths() {
        assertEquals("{\"type\":\"init\"}", json(Protocol.initRequest(null)));
        assertEquals("{\"type\":\"init\"}", json(Protocol.initRequest(Arrays.<String>asList())));
        assertEquals("{\"type\":\"init\",\"font_paths\":[\"/opt/fonts\",\"C:\\\\Fonts\"]}",
                json(Protocol.initRequest(Arrays.asList("/opt/fonts", "C:\\Fonts"))));
    }

    @Test
    void convertRequest_shape() {
        assertEquals("{\"type\":\"convert\",\"id\":7,\"input\":\"/abs/in.docx\",\"output\":\"/abs/out.pdf\",\"format\":1}",
                json(Protocol.convertRequest(7, "/abs/in.docx", "/abs/out.pdf", 1, null)));
    }

    @Test
    void convertBufferRequest_shape() {
        assertEquals("{\"type\":\"convert_buffer\",\"id\":8,\"format\":0,\"data_size\":48213}",
                json(Protocol.convertBufferRequest(8, 0, 48213, ConversionOptions.defaults())));
    }

    @Test
    void quitRequest_shape() {
        assertEquals("{\"type\":\"quit\"}", json(Protocol.quitRequest()));
    }

    @Test
    void options_allFieldsUseSnakeCase() {
        ConversionOptions opts = ConversionOptions.builder()
                .pageRange("1-3,5,8-")
                .password("s3cr\"et")
                .taggedPdf(true)
                .updateFields(true)
                .includeMarkup(true)
                .includeComments(true)
                .title("T")
                .author("A")
                .build();
        assertEquals("{\"page_range\":\"1-3,5,8-\",\"password\":\"s3cr\\\"et\",\"tagged_pdf\":true,"
                        + "\"update_fields\":true,\"include_markup\":true,\"include_comments\":true,"
                        + "\"title\":\"T\",\"author\":\"A\"}",
                json(Protocol.optionsJson(opts)));
    }

    @Test
    void options_defaultsAreOmitted() {
        assertEquals("{}", json(Protocol.optionsJson(ConversionOptions.defaults())));
        assertEquals("{}", json(Protocol.optionsJson(null)));
        assertFalse(Protocol.convertRequest(1, "a", "b", 0, ConversionOptions.defaults()).has("options"));
    }

    @Test
    void options_onlySetFieldsAreSent() {
        JsonObject o = Protocol.optionsJson(ConversionOptions.builder().pageRange("2").includeComments(true).build());
        assertEquals(JsonParser.parseString("{\"page_range\":\"2\",\"include_comments\":true}"), o);
    }

    // ---- Responses ----

    @Test
    void parseResult_success() throws ProtocolException {
        ResultHeader h = parse("{\"type\":\"result\",\"id\":1,\"success\":true,\"error_code\":null,"
                + "\"error_message\":null,\"page_count\":14,\"diagnostics\":[]}", "result", 1);
        assertTrue(h.success);
        assertEquals(14, h.pageCount);
        assertEquals(-1, h.dataSize);
        assertTrue(h.diagnostics.isEmpty());
        assertNull(h.errorCode);
    }

    @Test
    void parseResult_bufferSuccessHasDataSize() throws ProtocolException {
        ResultHeader h = parse("{\"type\":\"buffer_result\",\"id\":2,\"success\":true,\"error_code\":null,"
                + "\"error_message\":null,\"page_count\":1,\"diagnostics\":[],\"data_size\":1234}", "buffer_result", 2);
        assertEquals(1234, h.dataSize);
    }

    @Test
    void parseResult_bufferSuccessWithoutDataSizeIsProtocolError() {
        assertThrows(ProtocolException.class, () -> parse(
                "{\"type\":\"buffer_result\",\"id\":2,\"success\":true,\"page_count\":1}", "buffer_result", 2));
        assertThrows(ProtocolException.class, () -> parse(
                "{\"type\":\"buffer_result\",\"id\":2,\"success\":true,\"data_size\":-5}", "buffer_result", 2));
    }

    @Test
    void parseResult_failureMapsErrorCodes() throws ProtocolException {
        int[] codes = {1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 99};
        WordCraftPdfErrorCode[] expected = {
                WordCraftPdfErrorCode.INIT_FAILED, WordCraftPdfErrorCode.LOAD_FAILED, WordCraftPdfErrorCode.EXPORT_FAILED,
                WordCraftPdfErrorCode.INVALID_FORMAT, WordCraftPdfErrorCode.FILE_NOT_FOUND, WordCraftPdfErrorCode.OUT_OF_MEMORY,
                WordCraftPdfErrorCode.PERMISSION_DENIED, WordCraftPdfErrorCode.NOT_INITIALIZED,
                WordCraftPdfErrorCode.INVALID_ARGUMENT, WordCraftPdfErrorCode.PASSWORD_REQUIRED,
                WordCraftPdfErrorCode.WRONG_PASSWORD, WordCraftPdfErrorCode.UNKNOWN};
        for (int i = 0; i < codes.length; i++) {
            ResultHeader h = parse("{\"type\":\"result\",\"id\":1,\"success\":false,\"error_code\":" + codes[i]
                    + ",\"error_message\":\"boom\",\"diagnostics\":[]}", "result", 1);
            assertFalse(h.success);
            assertEquals(expected[i], h.errorCode);
            assertEquals("boom", h.errorMessage);
            assertEquals(0, h.pageCount);
        }
    }

    @Test
    void parseResult_failureWithUnknownOrMissingCode() throws ProtocolException {
        assertEquals(WordCraftPdfErrorCode.UNKNOWN,
                parse("{\"type\":\"result\",\"id\":1,\"success\":false,\"error_code\":42}", "result", 1).errorCode);
        ResultHeader h = parse("{\"type\":\"result\",\"id\":1,\"success\":false}", "result", 1);
        assertEquals(WordCraftPdfErrorCode.UNKNOWN, h.errorCode);
        assertEquals("Conversion failed", h.errorMessage);
    }

    @Test
    void parseResult_errorMessageIsProtocolError() {
        ProtocolException e = assertThrows(ProtocolException.class,
                () -> parse("{\"type\":\"error\",\"message\":\"invalid message: eof\"}", "result", 1));
        assertTrue(e.getMessage().contains("invalid message: eof"));
    }

    @Test
    void parseResult_wrongTypeOrIdIsProtocolError() {
        assertThrows(ProtocolException.class,
                () -> parse("{\"type\":\"buffer_result\",\"id\":1,\"success\":true}", "result", 1));
        assertThrows(ProtocolException.class,
                () -> parse("{\"type\":\"result\",\"id\":2,\"success\":true}", "result", 1));
        assertThrows(ProtocolException.class,
                () -> parse("{\"type\":\"result\",\"success\":true}", "result", 1));
        assertThrows(ProtocolException.class,
                () -> parse("{\"type\":\"result\",\"id\":1}", "result", 1));
        assertThrows(ProtocolException.class,
                () -> parse("{\"type\":\"result\",\"id\":1,\"success\":\"yes\"}", "result", 1));
    }

    @Test
    void parseDiagnostics_tolerantOfOddEntries() {
        List<ConversionDiagnostic> d = Protocol.parseDiagnostics(JsonParser.parseString(
                "[1, \"x\", null, {}, {\"severity\":\"WARNING\",\"category\":\"general\",\"message\":\"m\",\"font\":null},"
                        + "{\"severity\":\"bogus\",\"category\":7,\"message\":3}]"));
        assertEquals(3, d.size());
        assertEquals(DiagnosticSeverity.INFO, d.get(0).getSeverity());
        assertEquals(DiagnosticCategory.GENERAL, d.get(0).getCategory());
        assertEquals("", d.get(0).getMessage());
        assertEquals(DiagnosticSeverity.WARNING, d.get(1).getSeverity());
        assertEquals("m", d.get(1).getMessage());
        assertNull(d.get(1).getFontName());
        assertEquals(DiagnosticSeverity.INFO, d.get(2).getSeverity());
        assertTrue(Protocol.parseDiagnostics(null).isEmpty());
        assertTrue(Protocol.parseDiagnostics(JsonParser.parseString("{}")).isEmpty());
    }

    private static ResultHeader parse(String json, String type, long id) throws ProtocolException {
        return Protocol.parseResult(Protocol.parseObject(json.getBytes(StandardCharsets.UTF_8)), type, id);
    }

    private static String json(JsonObject o) {
        return new String(Protocol.toJsonBytes(o), StandardCharsets.UTF_8);
    }
}
