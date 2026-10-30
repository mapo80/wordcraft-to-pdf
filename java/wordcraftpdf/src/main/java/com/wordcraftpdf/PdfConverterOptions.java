package com.wordcraftpdf;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.TimeUnit;

/**
 * Configuration for creating a {@link PdfConverter}. Use {@link #builder()} to create instances.
 */
public final class PdfConverterOptions {

    private final String workerPath;
    private final List<String> fontDirectories;
    private final long conversionTimeoutMillis;
    private final long startupTimeoutMillis;
    private final int maxWorkers;
    private final int maxConversionsPerWorker;
    private final boolean warmUp;

    private PdfConverterOptions(Builder builder) {
        this.workerPath = builder.workerPath;
        this.fontDirectories = builder.fontDirectories == null
                ? null
                : Collections.unmodifiableList(new ArrayList<String>(builder.fontDirectories));
        this.conversionTimeoutMillis = builder.conversionTimeoutMillis;
        this.startupTimeoutMillis = builder.startupTimeoutMillis;
        this.maxWorkers = builder.maxWorkers;
        this.maxConversionsPerWorker = builder.maxConversionsPerWorker;
        this.warmUp = builder.warmUp;
    }

    /**
     * Path of the {@code wcpdf_worker} executable. Null (default) = locate it: system property
     * {@code wcpdf.worker.path}, environment variable {@code WCPDF_WORKER_PATH}, the worker
     * bundled in the JAR, then {@code output/bin/wcpdf_worker} above the working directory.
     */
    public String getWorkerPath() {
        return workerPath;
    }

    /**
     * Folders whose fonts (and their subfolders') are used ahead of the installed fonts.
     * Null means none.
     */
    public List<String> getFontDirectories() {
        return fontDirectories;
    }

    /** Maximum time for a single conversion, in milliseconds. Default: 300000 (5 minutes). */
    public long getConversionTimeoutMillis() {
        return conversionTimeoutMillis;
    }

    /**
     * Maximum time for a worker to start (font scan and warm-up conversion), in milliseconds.
     * Default: 120000 (2 minutes).
     */
    public long getStartupTimeoutMillis() {
        return startupTimeoutMillis;
    }

    /**
     * Maximum number of worker processes, i.e. of conversions running in parallel.
     * Workers start lazily, when every running one is busy. Default: the number of processors.
     */
    public int getMaxWorkers() {
        return maxWorkers;
    }

    /** Replace a worker after this many conversions. 0 (default) = never. */
    public int getMaxConversionsPerWorker() {
        return maxConversionsPerWorker;
    }

    /**
     * If true, {@link PdfConverter#create(PdfConverterOptions)} starts all {@link #getMaxWorkers()}
     * workers eagerly (in parallel). If false (default), they start on demand.
     */
    public boolean isWarmUp() {
        return warmUp;
    }

    public static Builder builder() {
        return new Builder();
    }

    public static final class Builder {
        private String workerPath;
        private List<String> fontDirectories;
        private long conversionTimeoutMillis = 5 * 60 * 1000L;
        private long startupTimeoutMillis = 2 * 60 * 1000L;
        private int maxWorkers = Math.max(1, Runtime.getRuntime().availableProcessors());
        private int maxConversionsPerWorker;
        private boolean warmUp;

        private Builder() {}

        public Builder workerPath(String workerPath) {
            this.workerPath = workerPath;
            return this;
        }

        public Builder fontDirectories(List<String> fontDirectories) {
            this.fontDirectories = fontDirectories;
            return this;
        }

        public Builder conversionTimeout(long duration, TimeUnit unit) {
            this.conversionTimeoutMillis = unit.toMillis(duration);
            return this;
        }

        public Builder conversionTimeoutMillis(long millis) {
            this.conversionTimeoutMillis = millis;
            return this;
        }

        public Builder startupTimeout(long duration, TimeUnit unit) {
            this.startupTimeoutMillis = unit.toMillis(duration);
            return this;
        }

        public Builder maxWorkers(int maxWorkers) {
            this.maxWorkers = maxWorkers;
            return this;
        }

        public Builder maxConversionsPerWorker(int maxConversionsPerWorker) {
            this.maxConversionsPerWorker = maxConversionsPerWorker;
            return this;
        }

        public Builder warmUp(boolean warmUp) {
            this.warmUp = warmUp;
            return this;
        }

        public PdfConverterOptions build() {
            if (maxWorkers < 1) {
                throw new IllegalArgumentException("maxWorkers must be at least 1");
            }
            if (maxConversionsPerWorker < 0) {
                throw new IllegalArgumentException("maxConversionsPerWorker must not be negative");
            }
            if (conversionTimeoutMillis <= 0) {
                throw new IllegalArgumentException("conversionTimeout must be positive");
            }
            if (startupTimeoutMillis <= 0) {
                throw new IllegalArgumentException("startupTimeout must be positive");
            }
            if (workerPath != null && workerPath.isEmpty()) {
                throw new IllegalArgumentException("workerPath must not be empty");
            }
            if (fontDirectories != null) {
                for (String dir : fontDirectories) {
                    if (dir == null || dir.isEmpty()) {
                        throw new IllegalArgumentException("fontDirectories must not contain null or empty entries");
                    }
                }
            }
            return new PdfConverterOptions(this);
        }
    }
}
