package com.wordcraftpdf.internal;

import org.junit.jupiter.api.Test;

import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;

import static org.junit.jupiter.api.Assertions.*;

class StderrTailTest {

    @Test
    void keepsEverythingBelowCapacity() {
        StderrTail t = new StderrTail(16);
        t.append(bytes("hello "), 0, 6);
        t.append(bytes("world"), 0, 5);
        assertEquals("hello world", t.toString());
        assertEquals(11, t.totalBytes());
    }

    @Test
    void keepsOnlyTheLastBytes() {
        StderrTail t = new StderrTail(8);
        for (char c = 'a'; c <= 'z'; c++) {
            t.append(new byte[] {(byte) c}, 0, 1);
        }
        assertEquals("stuvwxyz", new String(t.bytes(), StandardCharsets.US_ASCII));
        assertEquals("...stuvwxyz", t.toString());
        assertEquals(26, t.totalBytes());
    }

    @Test
    void wrapsAcrossTheRingBoundary() {
        StderrTail t = new StderrTail(8);
        t.append(bytes("123456"), 0, 6);
        t.append(bytes("abcdef"), 0, 6);
        assertEquals("56abcdef", new String(t.bytes(), StandardCharsets.US_ASCII));
    }

    @Test
    void chunkLargerThanCapacityKeepsItsTail() {
        StderrTail t = new StderrTail(4);
        t.append(bytes("xx"), 0, 2);
        t.append(bytes("0123456789"), 2, 8);
        assertEquals("6789", new String(t.bytes(), StandardCharsets.US_ASCII));
        assertEquals(10, t.totalBytes());
    }

    @Test
    void drainIsBoundedAndDaemon() throws Exception {
        byte[] big = new byte[5 * 1024 * 1024];
        for (int i = 0; i < big.length; i++) big[i] = (byte) ('a' + i % 26);
        big[big.length - 1] = 'Z';
        StderrTail t = new StderrTail(StderrTail.DEFAULT_CAPACITY);
        Thread th = t.drain(new ByteArrayInputStream(big), "test-drain");
        assertTrue(th.isDaemon());
        th.join(10000);
        assertFalse(th.isAlive());
        assertEquals(big.length, t.totalBytes());
        assertEquals(StderrTail.DEFAULT_CAPACITY, t.bytes().length);
        assertTrue(t.toString().endsWith("Z"));
    }

    private static byte[] bytes(String s) {
        return s.getBytes(StandardCharsets.US_ASCII);
    }
}
