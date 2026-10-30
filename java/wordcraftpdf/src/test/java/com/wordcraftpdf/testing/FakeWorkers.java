package com.wordcraftpdf.testing;

import com.google.gson.Gson;

import java.io.File;
import java.io.IOException;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.stream.Collectors;

/** Creates executable scripts that run {@link FakeWorker} in a separate JVM. */
public final class FakeWorkers {

    private FakeWorkers() {}

    /** Whether fake workers can run here (they are shell scripts). */
    public static boolean supported() {
        return !System.getProperty("os.name", "").toLowerCase(Locale.ROOT).startsWith("windows");
    }

    /** Write {@code dir/fake_worker_<mode>} running the fake worker in {@code mode}; return its path. */
    public static String script(Path dir, String mode) throws IOException {
        String java = new File(new File(System.getProperty("java.home"), "bin"), "java").getAbsolutePath();
        String classpath = location(FakeWorker.class) + File.pathSeparator + location(Gson.class);
        String text = "#!/bin/sh\n"
                + "FAKE_MODE='" + mode + "'\n"
                + "export FAKE_MODE\n"
                + "exec '" + java + "' -Xshare:auto -XX:TieredStopAtLevel=1 -XX:+UseSerialGC -Xmx64m"
                + " -cp '" + classpath + "' " + FakeWorker.class.getName() + "\n";
        Path script = dir.resolve("fake_worker_" + mode);
        Files.write(script, text.getBytes(StandardCharsets.UTF_8));
        if (!script.toFile().setExecutable(true, true)) {
            throw new IOException("cannot make " + script + " executable");
        }
        return script.toAbsolutePath().toString();
    }

    private static String location(Class<?> c) {
        try {
            return new File(c.getProtectionDomain().getCodeSource().getLocation().toURI()).getAbsolutePath();
        } catch (URISyntaxException e) {
            throw new IllegalStateException(e);
        }
    }

    /** Live descendants of this JVM whose command line contains {@code fragment}. */
    public static List<ProcessHandle> descendantsMatching(String fragment) {
        return ProcessHandle.current().descendants()
                .filter(ProcessHandle::isAlive)
                .filter(h -> commandLine(h).contains(fragment))
                .collect(Collectors.toCollection(ArrayList::new));
    }

    public static String commandLine(ProcessHandle h) {
        ProcessHandle.Info info = h.info();
        StringBuilder sb = new StringBuilder(info.command().orElse(""));
        info.arguments().ifPresent(args -> {
            for (String a : args) sb.append(' ').append(a);
        });
        return sb.toString();
    }
}
