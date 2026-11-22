# Playback dependency provenance and release gate

Playback dylibs are copied from the **IINA v1.5.1** release archive only. No IINA application implementation, icon or visual assets are used. Archive SHA-256: `f414507ec60bcfeda3a9f539f74470253e31b619d031e7a9e9a71519da1598cd`.

- Binary archive: https://github.com/iina/iina/releases/tag/v1.5.1
- IINA build/dependency records: https://github.com/iina/iina and https://github.com/iina/deps-buildscripts . IINA states that the latter replaced its old Homebrew based dependency process for 1.5.x; `versions.env`, `buildscripts/` and `patches/` are the relevant upstream records. The remaining source correspondence question concerns the rebuilt mpv library.
- mpv: https://github.com/mpv-player/mpv — default GPL-2.0-or-later; actual build configuration determines additional obligations.
- FFmpeg: https://ffmpeg.org/legal.html — actual enabled libraries/configuration determine GPL/LGPL and other obligations.
- libass: https://github.com/libass/libass

`DependencyManifest.json` inside the application records every bundled dylib and its original checksum. Original packaging thins to arm64, rewrites loader references, and ad-hoc signs binaries. The distribution candidate preserves that executable code and uses Developer ID signing. The archive contains multiple dependencies with their own terms. Neither the upstream archive URL nor these source links alone constitute complete corresponding-source compliance.

Provenance cross-check: the official [IINA 1.5.0 dependency release](https://github.com/iina/deps-buildscripts/releases/tag/iina-1.5.0-release) publishes an arm64 archive whose SHA-256 is `ec7266468a297b907eadbb473c7b3d2992cd274c40d379d8e0e8910caedfae50`. Its tag points to build-script commit `f86d54276547df6d14104583ea604af67433f1e1`, which pins FFmpeg 9.0.1, mpv 0.41.0, libjxl 0.11.2 and libplacebo 7.360.1. Its 44 dylib names match the IINA 1.5.1 payload. The original UUID comparison matched 39 of 44; subsequent direct arm64 executable-section comparisons match **43 of 44**. The remaining mpv library has matching normalized strings, C function symbols, configuration and patch markers, but different Swift/compiler output and SDK metadata. This supports source inference; it does not establish the exact production source or a reproducible build for mpv. See [DEPENDENCY_SOURCES.md](DEPENDENCY_SOURCES.md) for the verified source archive inventory, patches and reconstruction script.

**Public redistribution remains blocked** until exact upstream revisions, patches, configure/build scripts, all applicable notices and corresponding source archives for the actual binaries have been assembled and reviewed. Free distribution is not an exemption. The owner has now licensed the original application source under GPL-3.0-or-later; see LICENSE and COPYRIGHT. The current artifact is for local validation, not a license-cleared public release.

No cloud API credentials are included. B站 request logs record endpoint and numeric status only; diagnostics do not dump response bodies, user cookies or authorization headers.

Runtime inspection of the bundled FFmpeg reports **9.0.1**, `GPL version 2 or later`, `--enable-gpl` present and `--enable-nonfree` absent. The complete reported configuration is preserved in `artifacts/dependency-build-config.json`. This narrows the audit; it does not establish exact source/patch correspondence for every bundled component.
