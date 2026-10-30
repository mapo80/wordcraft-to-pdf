# WordCraftPdf.NativeAssets.Linux

**Native Linux worker for the WordCraftPdf converter.**

This package contains `wcpdf_worker`, the process the WordCraftPdf SDK runs conversions in. It
embeds the WordCraft engine (pure Rust) and speaks a small pipe protocol with the SDK; no native
code is loaded into your .NET process, so a crash in the engine only ever ends one worker.

The worker ships under `runtimes/<rid>/native/`; NuGet copies it to your build output and the
package's `.targets` keep it executable. Third-party licenses are in the package's
`licenses/` folder.

## Installation

Add it next to the managed package:

```xml
<PackageReference Include="WordCraftPdf" Version="0.1.0" />
<PackageReference Include="WordCraftPdf.NativeAssets.Linux" Version="0.1.0"
    Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
```

## System dependencies

The worker is a single Rust executable built against glibc (the C library of the build host);
run `ldd wcpdf_worker` to list the shared libraries a given build needs.

## Architectures

| Runtime Identifier | Architecture |
|---|---|
| `linux-x64` | x86_64 (glibc) |
| `linux-arm64` | ARM64 (glibc) |

## License

MPL-2.0 (WordCraftPdf). The engine and bundled fonts are under the licenses in `licenses/`.
