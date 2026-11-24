# Android 播放依赖

- Maven 坐标为 [dev.jdtech.mpv:libmpv:1.1.0](https://central.sonatype.com/artifact/dev.jdtech.mpv/libmpv/1.1.0)。
- 包装层与构建脚本见 [libmpv-android v1.1.0 源码](https://github.com/jarnedemeulemeester/libmpv-android/tree/v1.1.0)。其包装层采用 MIT，Maven AAR 标注 GPL-3.0-or-later。
- 本次打包前核对的 AAR SHA-256 为 `20fd972e70d8e73bf8900d911f3ae0eeae354606baeb0fe58caa1a6380e196aa`。
- AAR 中的 libmpv 为 0.42.0，包含 FFmpeg 9.0.2、libass 0.17.5 和 libplacebo 7.387.0 等原生依赖。完整版本列表和摘要一致的 AAR 见 [上游 v1.1.0 发布页](https://github.com/jarnedemeulemeester/libmpv-android/releases/tag/v1.1.0)。预览 APK 包含 arm64-v8a 和 x86_64。

## 0.4.0 预览版对应源码

- 应用源码和构建配置见 [Android 发布标签](https://github.com/yej07243-lang/DanmakuPlayer/tree/android-v0.4.0-preview/android)，采用 [GPL-3.0-or-later](../LICENSE)。
- 原生库和 JNI 包装层对应 [libmpv-android v1.1.0 源码快照](https://github.com/jarnedemeulemeester/libmpv-android/archive/85a2445098b7cba99576c2963b1417b17b4debdd.tar.gz)。APK 中两种架构的原生库均与该版本 AAR 逐文件匹配。
- 快照内的 [依赖版本清单](https://github.com/jarnedemeulemeester/libmpv-android/blob/85a2445098b7cba99576c2963b1417b17b4debdd/buildscripts/include/depinfo.sh) 和 [源码获取脚本](https://github.com/jarnedemeulemeester/libmpv-android/blob/85a2445098b7cba99576c2963b1417b17b4debdd/buildscripts/include/download-deps.sh) 固定了 mpv、FFmpeg、libass 等依赖的源码版本和下载位置。各依赖源码包含相应的许可和版权通知。原生库的编译步骤见 [上游构建说明](https://github.com/jarnedemeulemeester/libmpv-android/blob/85a2445098b7cba99576c2963b1417b17b4debdd/buildscripts/README.md)。
