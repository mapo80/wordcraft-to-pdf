package com.wordcraftpdf.internal;

import java.lang.reflect.Method;

/**
 * Forcible termination of a process and its descendants. Descendants are found through
 * {@code Process.descendants()} (Java 9+), called reflectively so the SDK still runs on Java 8,
 * where only the process itself can be killed.
 */
final class ProcessKiller {

    private static final Method DESCENDANTS;
    private static final Method HANDLE_DESTROY_FORCIBLY;

    static {
        Method descendants = null;
        Method destroy = null;
        try {
            descendants = Process.class.getMethod("descendants");
            destroy = Class.forName("java.lang.ProcessHandle").getMethod("destroyForcibly");
        } catch (Exception e) {
            descendants = null;
            destroy = null;
        }
        DESCENDANTS = descendants;
        HANDLE_DESTROY_FORCIBLY = destroy;
    }

    private ProcessKiller() {}

    /** Kill {@code process} and, on Java 9+, every process it started. Never throws. */
    static void destroyTree(Process process) {
        if (process == null) return;
        // Descendants first: once the parent is gone they are re-parented and can't be found.
        if (DESCENDANTS != null) {
            try {
                Object[] handles = ((java.util.stream.Stream<?>) DESCENDANTS.invoke(process)).toArray();
                for (Object handle : handles) {
                    try {
                        HANDLE_DESTROY_FORCIBLY.invoke(handle);
                    } catch (Exception ignored) {
                        // Already gone, or not ours to kill.
                    }
                }
            } catch (Exception ignored) {
                // Best effort.
            }
        }
        try {
            process.destroyForcibly();
        } catch (Exception ignored) {
            // Best effort.
        }
    }
}
