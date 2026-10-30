package com.wordcraftpdf;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.*;

class OptionsTest {

    @Test
    void conversionOptions_defaults() {
        ConversionOptions o = ConversionOptions.builder().build();
        assertNull(o.getPageRange());
        assertNull(o.getPassword());
        assertFalse(o.isTaggedPdf());
        assertFalse(o.isUpdateFields());
        assertFalse(o.isIncludeMarkup());
        assertFalse(o.isIncludeComments());
        assertNull(o.getTitle());
        assertNull(o.getAuthor());
        assertNotNull(ConversionOptions.defaults());
    }

    @Test
    void conversionOptions_customValues() {
        ConversionOptions o = ConversionOptions.builder()
                .pageRange("1-3")
                .password("secret")
                .taggedPdf(true)
                .updateFields(true)
                .includeMarkup(true)
                .includeComments(true)
                .title("Title")
                .author("Author")
                .build();
        assertEquals("1-3", o.getPageRange());
        assertEquals("secret", o.getPassword());
        assertTrue(o.isTaggedPdf());
        assertTrue(o.isUpdateFields());
        assertTrue(o.isIncludeMarkup());
        assertTrue(o.isIncludeComments());
        assertEquals("Title", o.getTitle());
        assertEquals("Author", o.getAuthor());
    }

    @Test
    void converterOptions_defaults() {
        PdfConverterOptions o = PdfConverterOptions.builder().build();
        assertNull(o.getWorkerPath());
        assertNull(o.getFontDirectories());
        assertEquals(300000, o.getConversionTimeoutMillis());
        assertEquals(120000, o.getStartupTimeoutMillis());
        assertEquals(Runtime.getRuntime().availableProcessors(), o.getMaxWorkers());
        assertEquals(0, o.getMaxConversionsPerWorker());
        assertFalse(o.isWarmUp());
    }

    @Test
    void converterOptions_customValues() {
        PdfConverterOptions o = PdfConverterOptions.builder()
                .workerPath("/opt/wcpdf_worker")
                .fontDirectories(Arrays.asList("/a", "/b"))
                .conversionTimeout(30, TimeUnit.SECONDS)
                .startupTimeout(10, TimeUnit.SECONDS)
                .maxWorkers(3)
                .maxConversionsPerWorker(50)
                .warmUp(true)
                .build();
        assertEquals("/opt/wcpdf_worker", o.getWorkerPath());
        assertEquals(Arrays.asList("/a", "/b"), o.getFontDirectories());
        assertEquals(30000, o.getConversionTimeoutMillis());
        assertEquals(10000, o.getStartupTimeoutMillis());
        assertEquals(3, o.getMaxWorkers());
        assertEquals(50, o.getMaxConversionsPerWorker());
        assertTrue(o.isWarmUp());
        assertEquals(1234, PdfConverterOptions.builder().conversionTimeoutMillis(1234).build().getConversionTimeoutMillis());
    }

    @Test
    void converterOptions_fontDirectoriesAreCopiedAndUnmodifiable() {
        List<String> dirs = new ArrayList<String>(Arrays.asList("/a"));
        PdfConverterOptions o = PdfConverterOptions.builder().fontDirectories(dirs).build();
        dirs.add("/b");
        assertEquals(1, o.getFontDirectories().size());
        assertThrows(UnsupportedOperationException.class, () -> o.getFontDirectories().add("/c"));
    }

    @Test
    void converterOptions_rejectInvalidValues() {
        assertThrows(IllegalArgumentException.class, () -> PdfConverterOptions.builder().maxWorkers(0).build());
        assertThrows(IllegalArgumentException.class, () -> PdfConverterOptions.builder().maxConversionsPerWorker(-1).build());
        assertThrows(IllegalArgumentException.class, () -> PdfConverterOptions.builder().conversionTimeoutMillis(0).build());
        assertThrows(IllegalArgumentException.class, () -> PdfConverterOptions.builder().startupTimeout(0, TimeUnit.SECONDS).build());
        assertThrows(IllegalArgumentException.class, () -> PdfConverterOptions.builder().workerPath("").build());
        assertThrows(IllegalArgumentException.class,
                () -> PdfConverterOptions.builder().fontDirectories(Arrays.asList("/a", "")).build());
        assertThrows(IllegalArgumentException.class,
                () -> PdfConverterOptions.builder().fontDirectories(Arrays.asList("/a", null)).build());
    }
}
