package com.wordcraftpdf.internal;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermission;
import java.security.MessageDigest;
import java.util.Set;

import static org.junit.jupiter.api.Assertions.*;

class WorkerLocatorTest {

    private static final String PLATFORM = "testos-testarch";
    private static final String EXE = "wcpdf_worker";

    @TempDir
    Path tmp;

    private final ClassLoader emptyLoader = new URLClassLoader(new URL[0], null);

    private WorkerLocator locator(String sysProp, String env, File cwd, ClassLoader resources, File jarDir) {
        return new WorkerLocator(sysProp, env, cwd, resources, tmp.resolve("cache").toFile(), jarDir, PLATFORM, EXE);
    }

    private Path executable(Path p) throws IOException {
        Files.createDirectories(p.getParent());
        Files.write(p, "#!/bin/sh\n".getBytes(StandardCharsets.US_ASCII));
        assertTrue(p.toFile().setExecutable(true));
        return p;
    }

    @Test
    void explicitPathWins() throws Exception {
        Path explicit = executable(tmp.resolve("explicit/wcpdf_worker"));
        Path sys = executable(tmp.resolve("sys/wcpdf_worker"));
        String found = locator(sys.toString(), null, tmp.toFile(), emptyLoader, null).find(explicit.toString());
        assertEquals(explicit.toAbsolutePath().toString(), found);
    }

    @Test
    void explicitPathMissingFailsInsteadOfFallingBack() throws Exception {
        Path sys = executable(tmp.resolve("sys/wcpdf_worker"));
        FileNotFoundException e = assertThrows(FileNotFoundException.class,
                () -> locator(sys.toString(), null, tmp.toFile(), emptyLoader, null).find(tmp.resolve("nope").toString()));
        assertTrue(e.getMessage().contains("PdfConverterOptions.workerPath"), e.getMessage());
    }

    @Test
    void systemPropertyBeforeEnvironment() throws Exception {
        Path sys = executable(tmp.resolve("sys/wcpdf_worker"));
        Path env = executable(tmp.resolve("env/wcpdf_worker"));
        assertEquals(sys.toString(), locator(sys.toString(), env.toString(), tmp.toFile(), emptyLoader, null).find(null));
    }

    @Test
    void systemPropertyMissingFails() {
        FileNotFoundException e = assertThrows(FileNotFoundException.class,
                () -> locator(tmp.resolve("x").toString(), null, tmp.toFile(), emptyLoader, null).find(null));
        assertTrue(e.getMessage().contains(WorkerLocator.SYSTEM_PROPERTY), e.getMessage());
    }

    @Test
    void environmentVariableUsed() throws Exception {
        Path env = executable(tmp.resolve("env/wcpdf_worker"));
        assertEquals(env.toString(), locator(null, env.toString(), tmp.toFile(), emptyLoader, null).find(null));
        assertEquals(env.toString(), locator("  ", env.toString(), tmp.toFile(), emptyLoader, null).find(""));
    }

    @Test
    void environmentVariableMissingFails() {
        FileNotFoundException e = assertThrows(FileNotFoundException.class,
                () -> locator(null, tmp.resolve("y").toString(), tmp.toFile(), emptyLoader, null).find(null));
        assertTrue(e.getMessage().contains(WorkerLocator.ENV_VAR), e.getMessage());
    }

    @Test
    void bundledWorkerIsExtractedOnceWithExecutePermission() throws Exception {
        byte[] content = "#!/bin/sh\necho bundled\n".getBytes(StandardCharsets.US_ASCII);
        ClassLoader jar = bundle(content, sha256(content));

        String first = locator(null, null, tmp.toFile(), jar, null).find(null);
        File f = new File(first);
        assertTrue(f.isFile());
        assertTrue(f.canExecute());
        assertArrayEquals(content, Files.readAllBytes(f.toPath()));
        assertTrue(first.startsWith(tmp.resolve("cache").toString()), first);
        // Keyed by version and content hash.
        assertTrue(f.getParentFile().getParentFile().getName().endsWith("-" + sha256(content).substring(0, 16)),
                f.getParentFile().getParentFile().getName());

        Set<PosixFilePermission> perms = Files.getPosixFilePermissions(f.toPath());
        assertFalse(perms.contains(PosixFilePermission.OTHERS_WRITE));
        assertFalse(perms.contains(PosixFilePermission.GROUP_WRITE));
        assertFalse(Files.getPosixFilePermissions(tmp.resolve("cache")).contains(PosixFilePermission.OTHERS_WRITE));

        long modified = f.lastModified();
        Thread.sleep(20);
        // A new locator (another converter) reuses the extracted file.
        assertEquals(first, locator(null, null, tmp.toFile(), jar, null).find(null));
        assertEquals(modified, f.lastModified());
    }

    @Test
    void bundledWorkerWithoutSidecarIsHashed() throws Exception {
        byte[] content = "#!/bin/sh\necho no sidecar\n".getBytes(StandardCharsets.US_ASCII);
        String found = locator(null, null, tmp.toFile(), bundle(content, null), null).find(null);
        assertTrue(new File(found).getParentFile().getParentFile().getName().endsWith("-" + sha256(content).substring(0, 16)));
    }

    @Test
    void differentContentGoesToADifferentFolder() throws Exception {
        byte[] a = "#!/bin/sh\necho a\n".getBytes(StandardCharsets.US_ASCII);
        byte[] b = "#!/bin/sh\necho b\n".getBytes(StandardCharsets.US_ASCII);
        String pa = locator(null, null, tmp.toFile(), bundle(a, sha256(a)), null).find(null);
        String pb = new WorkerLocator(null, null, tmp.toFile(), bundle(b, sha256(b)), tmp.resolve("cache").toFile(),
                null, PLATFORM, EXE).find(null);
        assertNotEquals(pa, pb);
        assertArrayEquals(b, Files.readAllBytes(new File(pb).toPath()));
    }

    @Test
    void corruptBundleIsRejected() throws Exception {
        byte[] content = "#!/bin/sh\n".getBytes(StandardCharsets.US_ASCII);
        String wrongHash = sha256("something else".getBytes(StandardCharsets.US_ASCII));
        FileNotFoundException e = assertThrows(FileNotFoundException.class,
                () -> locator(null, null, tmp.toFile(), bundle(content, wrongHash), null).find(null));
        assertTrue(e.getMessage().contains("SHA-256"), e.getMessage());
    }

    @Test
    void nextToJar() throws Exception {
        Path jarDir = Files.createDirectories(tmp.resolve("lib"));
        Path w = executable(jarDir.resolve("native").resolve(PLATFORM).resolve(EXE));
        assertEquals(w.toString(), locator(null, null, tmp.resolve("elsewhere").toFile(), emptyLoader, jarDir.toFile()).find(null));
    }

    @Test
    void devFallbackWalksUpFromWorkingDirectory() throws Exception {
        Path root = tmp.resolve("repo");
        Path w = executable(root.resolve("output/bin/wcpdf_worker"));
        Path cwd = Files.createDirectories(root.resolve("java/wordcraftpdf"));
        assertEquals(w.toString(), locator(null, null, cwd.toFile(), emptyLoader, null).find(null));
    }

    @Test
    void notFoundListsWhatWasSearched() throws Exception {
        Path cwd = Files.createDirectories(tmp.resolve("empty"));
        FileNotFoundException e = assertThrows(FileNotFoundException.class,
                () -> locator(null, null, cwd.toFile(), emptyLoader, tmp.resolve("lib").toFile()).find(null));
        String m = e.getMessage();
        assertTrue(m.contains("classpath:/native/" + PLATFORM + "/" + EXE), m);
        assertTrue(m.contains("output/bin/" + EXE), m);
        assertTrue(m.contains(WorkerLocator.ENV_VAR), m);
        assertFalse(m.contains("http"), "no URLs in error messages: " + m);
    }

    @Test
    void platformIds() {
        assertEquals("osx-arm64", WorkerLocator.platformId("Mac OS X", "aarch64"));
        assertEquals("osx-x64", WorkerLocator.platformId("Mac OS X", "x86_64"));
        assertEquals("linux-x64", WorkerLocator.platformId("Linux", "amd64"));
        assertEquals("linux-arm64", WorkerLocator.platformId("Linux", "aarch64"));
        assertEquals("win-x64", WorkerLocator.platformId("Windows 11", "amd64"));
        assertEquals("win-arm64", WorkerLocator.platformId("Windows Server 2022", "aarch64"));
        assertEquals("freebsd-x64", WorkerLocator.platformId("FreeBSD", "amd64"));
        assertNotNull(WorkerLocator.platformId());
    }

    @Test
    void sdkVersionComesFromFilteredProperties() {
        assertEquals("0.1.0", WorkerLocator.sdkVersion());
    }

    // ---- helpers ----

    private ClassLoader bundle(byte[] content, String sha256) throws IOException {
        Path root = Files.createTempDirectory(tmp, "jar");
        Path dir = Files.createDirectories(root.resolve("native").resolve(PLATFORM));
        Files.write(dir.resolve(EXE), content);
        if (sha256 != null) {
            Files.write(dir.resolve(EXE + ".sha256"), (sha256 + "  " + EXE + "\n").getBytes(StandardCharsets.US_ASCII));
        }
        return new URLClassLoader(new URL[] {root.toUri().toURL()}, null);
    }

    private static String sha256(byte[] data) throws Exception {
        StringBuilder sb = new StringBuilder();
        for (byte b : MessageDigest.getInstance("SHA-256").digest(data)) {
            sb.append(String.format("%02x", b));
        }
        return sb.toString();
    }
}
