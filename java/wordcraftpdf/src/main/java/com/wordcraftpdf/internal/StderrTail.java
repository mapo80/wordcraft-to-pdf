package com.wordcraftpdf.internal;

import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;

/**
 * Keeps the last {@code capacity} bytes written to a worker's stderr. The worker's stderr is
 * drained continuously (so the worker never blocks on a full pipe) but only its tail is kept,
 * for error messages: memory stays bounded however much the worker writes.
 */
public final class StderrTail {

    /** Default bytes kept: the last 64 KiB. */
    public static final int DEFAULT_CAPACITY = 64 * 1024;

    private final byte[] ring;
    private long total;

    public StderrTail(int capacity) {
        if (capacity <= 0) throw new IllegalArgumentException("capacity must be positive");
        this.ring = new byte[capacity];
    }

    /** Append bytes, overwriting the oldest ones beyond the capacity. */
    public synchronized void append(byte[] data, int offset, int length) {
        int cap = ring.length;
        if (length >= cap) {
            offset += length - cap;
            total += length - cap;
            length = cap;
        }
        int pos = (int) (total % cap);
        int first = Math.min(length, cap - pos);
        System.arraycopy(data, offset, ring, pos, first);
        if (first < length) {
            System.arraycopy(data, offset + first, ring, 0, length - first);
        }
        total += length;
    }

    /** Total bytes ever appended. */
    public synchronized long totalBytes() {
        return total;
    }

    /** The bytes kept, oldest first. */
    public synchronized byte[] bytes() {
        int cap = ring.length;
        if (total <= cap) {
            byte[] out = new byte[(int) total];
            System.arraycopy(ring, 0, out, 0, out.length);
            return out;
        }
        byte[] out = new byte[cap];
        int pos = (int) (total % cap);
        System.arraycopy(ring, pos, out, 0, cap - pos);
        System.arraycopy(ring, 0, out, cap - pos, pos);
        return out;
    }

    /** The tail decoded as UTF-8 and trimmed; prefixed with "..." when older output was dropped. */
    @Override
    public String toString() {
        String text = new String(bytes(), StandardCharsets.UTF_8).trim();
        synchronized (this) {
            return total > ring.length ? "..." + text : text;
        }
    }

    /** Start a daemon thread copying {@code stream} into this tail until end of stream. */
    public Thread drain(final InputStream stream, String threadName) {
        Thread t = new Thread(new Runnable() {
            @Override
            public void run() {
                byte[] buffer = new byte[8192];
                try {
                    int n;
                    while ((n = stream.read(buffer)) >= 0) {
                        append(buffer, 0, n);
                    }
                } catch (IOException e) {
                    // The process is gone.
                } finally {
                    try {
                        stream.close();
                    } catch (IOException ignored) {
                        // Nothing to do.
                    }
                }
            }
        }, threadName);
        t.setDaemon(true);
        t.start();
        return t;
    }
}
