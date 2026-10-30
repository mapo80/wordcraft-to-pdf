# WordCraftPdf.NativeAssets.macOS

**Native macOS worker for the WordCraftPdf converter.**

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
<PackageReference Include="WordCraftPdf.NativeAssets.macOS" Version="0.1.0"
    Condition="$([MSBuild]::IsOSPlatform('OSX'))" />
```

## System dependencies

None: the worker only uses macOS system frameworks.

## Architectures

| Runtime Identifier | Architecture |
|---|---|
| `osx-arm64` | Apple Silicon |
| `osx-x64` | Intel |

## License

MPL-2.0 (WordCraftPdf). The engine and bundled fonts are under the licenses in `licenses/`.
