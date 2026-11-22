# Playback dependency provenance and release gate

Playback dylibs are copied from the **IINA v1.5.1** release archive only. No IINA application implementation, icon or visual assets are used. Archive SHA-256: `f414507ec60bcfeda3a9f539f74470253e31b619d031e7a9e9a71519da1598cd`.

- Binary archive: https://github.com/iina/iina/releases/tag/v1.5.1
- IINA build/dependency records: https://github.com/iina/iina and https://github.com/iina/deps-buildscripts . IINA states that the latter replaced its old Homebrew based dependency process for 1.5.x; `versions.env`, `buildscripts/` and `patches/` are the relevant upstream records. Their exact revision still must be matched to the downloaded 1.5.1 binaries.
- mpv: https://github.com/mpv-player/mpv — default GPL-2.0-or-later; actual build configuration determines additional obligations.
- FFmpeg: https://ffmpeg.org/legal.html — actual enabled libraries/configuration determine GPL/LGPL and other obligations.
- libass: https://github.com/libass/libass

`DependencyManifest.json` inside the application records every bundled dylib and its original checksum. Packaging thins to arm64, rewrites loader references, and ad-hoc signs binaries. The archive contains multiple dependencies with their own terms. Neither the upstream archive URL nor these source links alone constitute complete corresponding-source compliance.

Provenance cross-check: the official [IINA 1.5.0 dependency release](https://github.com/iina/deps-buildscripts/releases/tag/iina-1.5.0-release) publishes a universal archive whose SHA-256 is `d6b3bb920f14780ec9c9df22e323e2bf72a5ca2b198473c67bf37581f0a60b6e`. Its tag points to build-script commit `f86d54276547df6d14104583ea604af67433f1e1`, which pins FFmpeg 9.0.1, mpv 0.41.0, libjxl 0.11.2 and libplacebo 7.360.1. Its 44 dylib names match the IINA 1.5.1 payload. `artifacts/dependency-uuid-match.json` compares both architecture UUIDs: **39 of 44 match**, while `libSvtAv1Enc.4`, `libjpeg.62`, `libmpv.2`, `libvorbis.0`, and `libvorbisenc.2` differ. Raw file hashes differ for all 44; signing or packaging changes may account for some differences, but this has not been proven per file. The matching UUIDs narrow provenance, but the five rebuilt libraries and their exact build-script revision are unresolved. Do not present the 1.5.0 archive as complete corresponding source for the 1.5.1 payload.

**Public redistribution remains blocked** until exact upstream revisions, patches, configure/build scripts, all applicable notices and corresponding source archives for the actual binaries have been assembled and reviewed. Free distribution is not an exemption. The owner has now licensed the original application source under GPL-3.0-or-later; see LICENSE and COPYRIGHT. The current artifact is for local validation, not a license-cleared public release.

No cloud API credentials are included. B站 request logs record endpoint and numeric status only; diagnostics do not dump response bodies, user cookies or authorization headers.

Runtime inspection of the bundled FFmpeg reports **9.0.1**, `GPL version 2 or later`, `--enable-gpl` present and `--enable-nonfree` absent. The complete reported configuration is preserved in `artifacts/dependency-build-config.json`. This narrows the audit; it does not establish exact source/patch correspondence for every bundled component.
