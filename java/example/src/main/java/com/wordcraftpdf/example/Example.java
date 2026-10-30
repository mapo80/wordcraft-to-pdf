package com.wordcraftpdf.example;

import com.wordcraftpdf.ConversionDiagnostic;
import com.wordcraftpdf.ConversionResult;
import com.wordcraftpdf.PdfConverter;
import com.wordcraftpdf.PdfConverterOptions;

import java.io.File;
import java.util.Arrays;

/**
 * Converts one document to PDF.
 *
 * <pre>
 * mvn -q -pl example exec:java -Dexec.args="input.docx [output.pdf] [font-dir...]"
 * </pre>
 */
public class Example {

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.err.println("Usage: Example <input> [output.pdf] [font-dir...]");
            System.exit(1);
        }

        String input = args[0];
        String output = args.length > 1 ? args[1] : input.replaceAll("\\.[^./\\\\]+$", "") + ".pdf";

        PdfConverterOptions.Builder options = PdfConverterOptions.builder().maxWorkers(1);
        if (args.length > 2) {
            options.fontDirectories(Arrays.asList(args).subList(2, args.length));
        }

        System.out.println("Converting: " + input + " -> " + output);

        try (PdfConverter converter = PdfConverter.create(options.build())) {
            long start = System.nanoTime();
            ConversionResult result = converter.convert(input, output);
            long ms = (System.nanoTime() - start) / 1_000_000;

            if (!result.isSuccess()) {
                System.err.println("FAILED (" + result.getErrorCode() + "): " + result.getErrorMessage());
                System.exit(1);
            }
            System.out.println("OK: " + result.getPageCount() + " page(s), "
                    + String.format("%,d", new File(output).length()) + " bytes, " + ms + " ms"
                    + " [" + converter.getVersion() + "]");
            for (ConversionDiagnostic d : result.getDiagnostics()) {
                System.out.println("  " + d);
            }
        }
    }
}
