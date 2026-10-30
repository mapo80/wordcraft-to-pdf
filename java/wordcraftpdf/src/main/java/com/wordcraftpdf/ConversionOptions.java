package com.wordcraftpdf;

/**
 * Options for a single conversion. Every option defaults to off / unset.
 * Use {@link #builder()} to create instances; instances are immutable and thread-safe.
 */
public final class ConversionOptions {

    private static final ConversionOptions DEFAULTS = builder().build();

    private final String pageRange;
    private final String password;
    private final boolean taggedPdf;
    private final boolean updateFields;
    private final boolean includeMarkup;
    private final boolean includeComments;
    private final String title;
    private final String author;

    private ConversionOptions(Builder builder) {
        this.pageRange = builder.pageRange;
        this.password = builder.password;
        this.taggedPdf = builder.taggedPdf;
        this.updateFields = builder.updateFields;
        this.includeMarkup = builder.includeMarkup;
        this.includeComments = builder.includeComments;
        this.title = builder.title;
        this.author = builder.author;
    }

    /** Options with every field at its default. */
    public static ConversionOptions defaults() {
        return DEFAULTS;
    }

    /** 1-based pages to export, e.g. {@code "1-3,5,8-"} ({@code 8-} = page 8 to the end). Null = all. */
    public String getPageRange() {
        return pageRange;
    }

    /** Password of an encrypted document. Null = none. */
    public String getPassword() {
        return password;
    }

    /** Whether to write a structure tree (tagged PDF, for accessibility). */
    public boolean isTaggedPdf() {
        return taggedPdf;
    }

    /** Whether to refresh fields (table of contents, page references...) before export. */
    public boolean isUpdateFields() {
        return updateFields;
    }

    /** Whether to show tracked changes as markup instead of the final text. */
    public boolean isIncludeMarkup() {
        return includeMarkup;
    }

    /** With {@link #isIncludeMarkup()}, whether to show comments in balloons beside each page. */
    public boolean isIncludeComments() {
        return includeComments;
    }

    /** PDF title. Null = the document's title property. */
    public String getTitle() {
        return title;
    }

    /** PDF author. Null = the document's author property. */
    public String getAuthor() {
        return author;
    }

    public static Builder builder() {
        return new Builder();
    }

    public static final class Builder {
        private String pageRange;
        private String password;
        private boolean taggedPdf;
        private boolean updateFields;
        private boolean includeMarkup;
        private boolean includeComments;
        private String title;
        private String author;

        private Builder() {}

        public Builder pageRange(String pageRange) {
            this.pageRange = pageRange;
            return this;
        }

        public Builder password(String password) {
            this.password = password;
            return this;
        }

        public Builder taggedPdf(boolean taggedPdf) {
            this.taggedPdf = taggedPdf;
            return this;
        }

        public Builder updateFields(boolean updateFields) {
            this.updateFields = updateFields;
            return this;
        }

        public Builder includeMarkup(boolean includeMarkup) {
            this.includeMarkup = includeMarkup;
            return this;
        }

        public Builder includeComments(boolean includeComments) {
            this.includeComments = includeComments;
            return this;
        }

        public Builder title(String title) {
            this.title = title;
            return this;
        }

        public Builder author(String author) {
            this.author = author;
            return this;
        }

        public ConversionOptions build() {
            return new ConversionOptions(this);
        }
    }
}
