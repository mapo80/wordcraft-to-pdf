package com.wordcraftpdf;

import java.util.Locale;

/**
 * Input document format. Values match the worker protocol's {@code format} codes.
 */
public enum DocumentFormat {
    /**
     * Let the worker decide: from the file extension in file mode, from the content in buffer
     * mode (DOCX, DOC, ODT and RTF are recognised; HTML, Markdown and text need an explicit format).
     */
    UNKNOWN(0),
    /** Word 2007+ (.docx, .docm, .dotx, .dotm). */
    DOCX(1),
    /** Word 97-2003 (.doc, .dot). */
    DOC(2),
    /** OpenDocument Text (.odt). */
    ODT(3),
    /** Rich Text Format (.rtf). */
    RTF(4),
    /** HTML (.html, .htm). */
    HTML(5),
    /** Markdown (.md, .markdown). */
    MARKDOWN(6),
    /** Plain text (.txt). */
    TEXT(7);

    private final int value;

    DocumentFormat(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    /** The format for a protocol value, or {@link #UNKNOWN}. */
    public static DocumentFormat fromValue(int value) {
        for (DocumentFormat f : values()) {
            if (f.value == value) return f;
        }
        return UNKNOWN;
    }

    /**
     * Detect the format from a file name's extension (case-insensitive), the same way the
     * worker does. Returns {@link #UNKNOWN} when the extension is not a supported format.
     */
    public static DocumentFormat fromExtension(String path) {
        if (path == null) return UNKNOWN;
        int slash = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
        String name = path.substring(slash + 1);
        int dot = name.lastIndexOf('.');
        if (dot < 0) return UNKNOWN;
        String ext = name.substring(dot + 1).toLowerCase(Locale.ROOT);
        switch (ext) {
            case "docx":
            case "docm":
            case "dotx":
            case "dotm":
                return DOCX;
            case "doc":
            case "dot":
                return DOC;
            case "odt":
                return ODT;
            case "rtf":
                return RTF;
            case "html":
            case "htm":
                return HTML;
            case "md":
            case "markdown":
                return MARKDOWN;
            case "txt":
                return TEXT;
            default:
                return UNKNOWN;
        }
    }
}
