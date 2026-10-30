package com.wordcraftpdf.internal;

import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.FileAlreadyExistsException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.attribute.PosixFileAttributeView;
import java.nio.file.attribute.PosixFilePermission;
import java.nio.file.attribute.PosixFilePermissions;
import java.security.CodeSource;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.EnumSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Properties;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Finds the {@code wcpdf_worker} executable. Search order:
 * <ol>
 *   <li>the path given in {@code PdfConverterOptions.workerPath};</li>
 *   <li>the {@code wcpdf.worker.path} system property;</li>
 *   <li>the {@code WCPDF_WORKER_PATH} environment variable;</li>
 *   <li>the worker bundled in the JAR as {@code /native/<platform>/wcpdf_worker[.exe]}, extracted
 *       once to a per-user cache folder keyed by SDK version and content hash (override the folder
 *       with the {@code wcpdf.cache.dir} system property);</li>
 *   <li>next to the SDK JAR: {@code wcpdf_worker}, {@code native/wcpdf_worker},
 *       {@code native/<platform>/wcpdf_worker};</li>
 *   <li>development fallback: {@code output/bin/wcpdf_worker} in the working directory or any of
 *       its parents.</li>
 * </ol>
 * The first three are explicit: when one is set but names no file, the search fails instead of
 * silently using another worker.
 */
public final class WorkerLocator {

    public static final String SYSTEM_PROPERTY = "wcpdf.worker.path";
    public static final String ENV_VAR = "WCPDF_WORKER_PATH";
    public static final String CACHE_DIR_PROPERTY = "wcpdf.cache.dir";

    /** Bundled workers already extracted by this JVM, by resource path. */
    private static final Map<String, String> EXTRACTED = new ConcurrentHashMap<String, String>();

    private final String systemPropertyValue;
    private final String envValue;
    private final File workingDir;
    private final ClassLoader resources;
    private final File cacheRoot;
    private final File jarDir;
    private final String platform;
    private final String executableName;

    WorkerLocator(String systemPropertyValue, String envValue, File workingDir, ClassLoader resources,
                  File cacheRoot, File jarDir, String platform, String executableName) {
        this.systemPropertyValue = systemPropertyValue;
        this.envValue = envValue;
        this.workingDir = workingDir;
        this.resources = resources;
        this.cacheRoot = cacheRoot;
        this.jarDir = jarDir;
        this.platform = platform;
        this.executableName = executableName;
    }

    /** A locator for this JVM's environment. */
    static WorkerLocator forCurrentEnvironment() {
        String cache = System.getProperty(CACHE_DIR_PROPERTY);
        File cacheRoot = cache != null && !cache.isEmpty()
                ? new File(cache)
                : new File(System.getProperty("java.io.tmpdir"), "wordcraftpdf-" + sanitize(System.getProperty("user.name", "user")));
        ClassLoader cl = WorkerLocator.class.getClassLoader();
        return new WorkerLocator(
                System.getProperty(SYSTEM_PROPERTY),
                System.getenv(ENV_VAR),
                new File(System.getProperty("user.dir", ".")),
                cl != null ? cl : ClassLoader.getSystemClassLoader(),
                cacheRoot,
                jarDirectory(),
                platformId(),
                executableName());
    }

    /**
     * Find the worker executable.
     *
     * @param explicitPath {@code PdfConverterOptions.workerPath}, or null.
     * @return the absolute path of the executable.
     * @throws FileNotFoundException if no worker is found; the message lists what was searched.
     */
    public static String findWorkerExecutable(String explicitPath) throws FileNotFoundException {
        return forCurrentEnvironment().find(explicitPath);
    }

    String find(String explicitPath) throws FileNotFoundException {
        if (isSet(explicitPath)) {
            return requireFile(explicitPath, "PdfConverterOptions.workerPath");
        }
        if (isSet(systemPropertyValue)) {
            return requireFile(systemPropertyValue, "system property " + SYSTEM_PROPERTY);
        }
        if (isSet(envValue)) {
            return requireFile(envValue, "environment variable " + ENV_VAR);
        }

        List<String> searched = new ArrayList<String>();

        String resource = "native/" + platform + "/" + executableName;
        searched.add("classpath:/" + resource);
        if (resources.getResource(resource) != null) {
            try {
                return extractBundled(resource);
            } catch (IOException e) {
                FileNotFoundException fnf = new FileNotFoundException(
                        "Failed to extract the bundled worker /" + resource + ": " + e.getMessage());
                fnf.initCause(e);
                throw fnf;
            }
        }

        if (jarDir != null) {
            File[] nearJar = {
                    new File(jarDir, executableName),
                    new File(new File(jarDir, "native"), executableName),
                    new File(new File(new File(jarDir, "native"), platform), executableName),
            };
            for (File f : nearJar) {
                searched.add(f.getPath());
                if (isExecutableFile(f)) return f.getAbsolutePath();
            }
        }

        for (File dir = workingDir.getAbsoluteFile(); dir != null; dir = dir.getParentFile()) {
            File f = new File(new File(new File(dir, "output"), "bin"), executableName);
            if (isExecutableFile(f)) return f.getAbsolutePath();
        }
        searched.add("output/bin/" + executableName + " in " + workingDir.getAbsolutePath() + " and its parents");

        StringBuilder sb = new StringBuilder();
        for (String s : searched) {
            if (sb.length() > 0) sb.append(", ");
            sb.append(s);
        }
        throw new FileNotFoundException("Cannot find the " + executableName + " executable for platform "
                + platform + ". Searched: " + sb + ". Set PdfConverterOptions.workerPath, the "
                + SYSTEM_PROPERTY + " system property or the " + ENV_VAR
                + " environment variable, or use a JAR that bundles the worker for this platform.");
    }

    // ------------------------------------------------------------------------------------------
    // Bundled worker extraction

    private String extractBundled(String resource) throws IOException {
        String cached = EXTRACTED.get(cacheKey(resource));
        if (cached != null && new File(cached).isFile()) {
            return cached;
        }
        synchronized (EXTRACTED) {
            cached = EXTRACTED.get(cacheKey(resource));
            if (cached != null && new File(cached).isFile()) {
                return cached;
            }
            String path = extract(resource);
            EXTRACTED.put(cacheKey(resource), path);
            return path;
        }
    }

    private String cacheKey(String resource) {
        return cacheRoot.getAbsolutePath() + "|" + System.identityHashCode(resources) + "|" + resource;
    }

    private String extract(String resource) throws IOException {
        String expectedHash = readSidecarHash(resource + ".sha256");
        String hash = expectedHash != null ? expectedHash : hashResource(resource);

        Path root = secureDirectory(cacheRoot.toPath());
        Path dir = root.resolve(sanitize(sdkVersion()) + "-" + hash.substring(0, 16)).resolve(platform);
        Files.createDirectories(dir);
        Path target = dir.resolve(executableName);
        if (isExecutableFile(target.toFile())) {
            // Written by an atomic rename, so complete; the folder name pins the content.
            return target.toAbsolutePath().toString();
        }

        Path tmp = Files.createTempFile(dir, executableName, ".tmp");
        try {
            String actual;
            InputStream in = resources.getResourceAsStream(resource);
            if (in == null) throw new FileNotFoundException("classpath:/" + resource);
            try {
                OutputStream out = Files.newOutputStream(tmp);
                try {
                    actual = copyAndHash(in, out);
                } finally {
                    out.close();
                }
            } finally {
                in.close();
            }
            if (expectedHash != null && !expectedHash.equalsIgnoreCase(actual)) {
                throw new IOException("bundled worker does not match its SHA-256 (" + expectedHash + "): the JAR is corrupt");
            }
            makeExecutable(tmp);
            try {
                Files.move(tmp, target, StandardCopyOption.ATOMIC_MOVE);
            } catch (AtomicMoveNotSupportedException e) {
                Files.move(tmp, target, StandardCopyOption.REPLACE_EXISTING);
            } catch (FileAlreadyExistsException e) {
                // Another process extracted it first (Windows refuses to replace).
            } catch (IOException e) {
                if (!isExecutableFile(target.toFile())) throw e;
            }
        } finally {
            Files.deleteIfExists(tmp);
        }
        return target.toAbsolutePath().toString();
    }

    /**
     * The cache root, created owner-only. A pre-existing root that another user owns or that others
     * can write to is not trusted (someone could plant a binary there): a fresh private temporary
     * folder is used instead.
     */
    private static Path secureDirectory(Path root) throws IOException {
        boolean posix = Files.getFileStore(existingAncestor(root)).supportsFileAttributeView(PosixFileAttributeView.class);
        if (!Files.exists(root)) {
            try {
                if (posix) {
                    Files.createDirectories(root, PosixFilePermissions.asFileAttribute(
                            EnumSet.of(PosixFilePermission.OWNER_READ, PosixFilePermission.OWNER_WRITE,
                                    PosixFilePermission.OWNER_EXECUTE)));
                } else {
                    Files.createDirectories(root);
                }
            } catch (FileAlreadyExistsException ignored) {
                // Created concurrently; checked below.
            }
        }
        if (!posix) {
            return root;
        }
        try {
            String owner = Files.getOwner(root).getName();
            Set<PosixFilePermission> perms = Files.getPosixFilePermissions(root);
            boolean trusted = Files.isDirectory(root)
                    && !Files.isSymbolicLink(root)
                    && owner.equals(System.getProperty("user.name"))
                    && !perms.contains(PosixFilePermission.GROUP_WRITE)
                    && !perms.contains(PosixFilePermission.OTHERS_WRITE);
            if (trusted) {
                return root;
            }
        } catch (IOException | UnsupportedOperationException ignored) {
            // Fall through to a private folder.
        }
        return Files.createTempDirectory("wordcraftpdf-");
    }

    private static Path existingAncestor(Path p) {
        Path a = p.toAbsolutePath();
        while (a != null && !Files.exists(a)) {
            a = a.getParent();
        }
        return a != null ? a : p.toAbsolutePath().getRoot();
    }

    private String readSidecarHash(String resource) throws IOException {
        InputStream in = resources.getResourceAsStream(resource);
        if (in == null) return null;
        try {
            byte[] buf = new byte[256];
            int n = 0;
            int r;
            while (n < buf.length && (r = in.read(buf, n, buf.length - n)) > 0) {
                n += r;
            }
            String text = new String(buf, 0, n, StandardCharsets.US_ASCII).trim();
            String token = text.split("\\s+")[0];
            return token.matches("[0-9a-fA-F]{64}") ? token.toLowerCase(Locale.ROOT) : null;
        } finally {
            in.close();
        }
    }

    private String hashResource(String resource) throws IOException {
        InputStream in = resources.getResourceAsStream(resource);
        if (in == null) throw new FileNotFoundException("classpath:/" + resource);
        try {
            return copyAndHash(in, null);
        } finally {
            in.close();
        }
    }

    private static String copyAndHash(InputStream in, OutputStream out) throws IOException {
        MessageDigest digest;
        try {
            digest = MessageDigest.getInstance("SHA-256");
        } catch (NoSuchAlgorithmException e) {
            throw new IOException(e);
        }
        byte[] buffer = new byte[256 * 1024];
        int n;
        while ((n = in.read(buffer)) >= 0) {
            digest.update(buffer, 0, n);
            if (out != null) out.write(buffer, 0, n);
        }
        StringBuilder hex = new StringBuilder(64);
        for (byte b : digest.digest()) {
            hex.append(Character.forDigit((b >> 4) & 0xF, 16)).append(Character.forDigit(b & 0xF, 16));
        }
        return hex.toString();
    }

    private static void makeExecutable(Path file) throws IOException {
        try {
            Files.setPosixFilePermissions(file, EnumSet.of(
                    PosixFilePermission.OWNER_READ, PosixFilePermission.OWNER_WRITE, PosixFilePermission.OWNER_EXECUTE));
        } catch (UnsupportedOperationException e) {
            if (!file.toFile().setExecutable(true, true)) {
                file.toFile().setExecutable(true);
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Helpers

    private static boolean isSet(String s) {
        return s != null && !s.trim().isEmpty();
    }

    private static String requireFile(String path, String source) throws FileNotFoundException {
        File f = new File(path.trim()).getAbsoluteFile();
        if (!f.isFile()) {
            throw new FileNotFoundException("Worker executable not found at '" + f.getPath() + "' (from " + source + ")");
        }
        return f.getPath();
    }

    private static boolean isExecutableFile(File f) {
        return f.isFile() && (isWindows() || f.canExecute());
    }

    static String sanitize(String s) {
        String clean = s.replaceAll("[^A-Za-z0-9._-]", "_");
        return clean.isEmpty() ? "_" : clean;
    }

    /** The SDK version, from the properties file Maven filters at build time. */
    static String sdkVersion() {
        InputStream in = WorkerLocator.class.getResourceAsStream("/com/wordcraftpdf/wordcraftpdf.properties");
        if (in != null) {
            try {
                Properties p = new Properties();
                p.load(in);
                String v = p.getProperty("version");
                if (v != null && !v.isEmpty() && !v.startsWith("${")) return v;
            } catch (IOException ignored) {
                // Fall through.
            } finally {
                try {
                    in.close();
                } catch (IOException ignored) {
                    // Nothing to do.
                }
            }
        }
        return "dev";
    }

    /** The JAR's folder (or the classes folder when running from a build tree); null if unknown. */
    static File jarDirectory() {
        try {
            CodeSource cs = WorkerLocator.class.getProtectionDomain().getCodeSource();
            if (cs != null && cs.getLocation() != null) {
                File file = new File(cs.getLocation().toURI());
                return file.isFile() ? file.getParentFile() : file;
            }
        } catch (URISyntaxException | SecurityException | IllegalArgumentException ignored) {
            // Unknown.
        }
        return null;
    }

    /** The platform folder name: {@code <os>-<arch>}, e.g. {@code osx-arm64}, {@code linux-x64}, {@code win-x64}. */
    public static String platformId() {
        return platformId(System.getProperty("os.name", ""), System.getProperty("os.arch", ""));
    }

    static String platformId(String osName, String osArch) {
        String os = osName.toLowerCase(Locale.ROOT);
        String osId;
        if (os.contains("mac") || os.contains("darwin")) {
            osId = "osx";
        } else if (os.startsWith("windows")) {
            osId = "win";
        } else if (os.contains("linux")) {
            osId = "linux";
        } else {
            osId = sanitize(os.replace(' ', '-'));
        }
        String arch = osArch.toLowerCase(Locale.ROOT);
        String archId;
        if (arch.equals("aarch64") || arch.equals("arm64")) {
            archId = "arm64";
        } else if (arch.equals("amd64") || arch.equals("x86_64") || arch.equals("x64")) {
            archId = "x64";
        } else {
            archId = sanitize(arch);
        }
        return osId + "-" + archId;
    }

    public static String executableName() {
        return isWindows() ? "wcpdf_worker.exe" : "wcpdf_worker";
    }

    static boolean isWindows() {
        return System.getProperty("os.name", "").toLowerCase(Locale.ROOT).startsWith("windows");
    }
}
