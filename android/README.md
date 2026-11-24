# DanmakuPlayer Android 0.4.0 预览版

- 媒体库顶部可以搜索和导入视频或文件夹，筛选直接显示在列表上方。剧集按季展示，继续观看卡片显示当前集和进度。每集可直接播放，也可在「更多」里从头播放、重新定位或移除。宽窗口并排显示媒体列表和详情。
- 播放器单击显示或隐藏控件，两侧双击快退或快进 5 秒。时间轴显示当前和总时长，字幕、音轨、弹幕开关和全屏可直接操作，倍速在「更多」中。
- 「弹幕设置」包含「来源」「显示」「时间校准」。来源可搜片名、粘贴 B 站链接或导入本地 XML。确认分 P 或单集时会展示来源和时长，关联成功后才保存新来源。宽窗口中可同时查看搜索候选和分 P。
- 显示页可实时调整字号、不透明度、区域、速度、同屏上限和屏蔽词。校准页可调整整体延后，并查看和编辑本地时间与来源时间的对应点。正偏移让弹幕更晚出现。
- 「媒体库选项」提供重扫、管理导入位置、版本信息和诊断日志导出。

页面用 Kotlin 和 Android 系统控件。B 站可能要求网页验证或限制公开接口访问，可使用可公开解析的完整视频链接或本地 XML。

逐项状态和剩余测试见 [Android 与 macOS 功能对照](MACOS-PARITY.md)。

最低 Android 8.0（API 26）。APK 包含 `arm64-v8a` 和 `x86_64`。Apple 芯片 Mac 使用 arm64 模拟器镜像，Intel/AMD 主机可使用 x86_64。Gradle 使用 API 37 编译，以满足 libmpv AAR 的要求。预编译 AAR 已包含原生库，复用 AAR 时无需本地编译 libmpv。依赖来源见 [THIRD_PARTY.md](THIRD_PARTY.md)。

## 下载

预览版 APK 和更新说明见 [Android 发布页](https://github.com/yej07243-lang/DanmakuPlayer/releases/tag/android-v0.4.0-preview)。

## 构建

准备 Android SDK Platform 37、Build Tools 37.0.0 和 JDK 21。当前 Mac 可在本次命令中使用 Android Studio 自带的 JDK。

```sh
cd android
export JAVA_HOME="/Applications/Android Studio.app/Contents/jbr/Contents/Home"
export ANDROID_HOME="$HOME/Library/Android/sdk"
./gradlew :app:assembleDebug
```

Gradle 产物在 `app/build/outputs/apk/debug/app-debug.apk`。0.4.0 预览 APK 用 Android Studio 的 Kotlin 和 SDK 37 的 AAPT2、D8、zipalign、apksigner 打包，沿用 0.3.0 的测试签名。

## 复测播放

```sh
# 在仓库根目录执行。将下载的 APK 放在当前目录，FFmpeg 需要带 libx264 编码器。
./android/smoke/generate-sample.sh artifacts/android-smoke
adb install -r DanmakuPlayer-0.4.0-preview.apk
adb push artifacts/android-smoke/双音轨-ASS.mkv artifacts/android-smoke/external.ass /sdcard/Download/
adb logcat -s DanmakuMpv DanmakuBili
```

| 步骤 | 通过条件 |
| --- | --- |
| 导入下载目录里的 MKV 并打开 | 看到 20 秒彩色动态图，时间向前走，能暂停和拖动 |
| 选择音轨 | 440 Hz 和 880 Hz 相互切换时，音高和选中项随之改变 |
| 检查字幕 | 画面下方出现 `EMBEDDED ASS`。手动选 `external.ass` 后，继续播放并确认上方出现 `EXTERNAL ASS` |
| 检查解码 | 真机播放时记录页面的解码值和 `DanmakuMpv` 日志。若显示软件解码，记录设备型号、Android 版本与 MediaCodec 日志 |

## 0.4.0 验收记录

| 环境 | 片源或素材 | 本轮状态 |
| --- | --- | --- |
| Mac 离线打包 | APK，版本代码 4 | Kotlin 编译、APK 签名和对齐通过。包名、签名证书与 0.3.0 一致，包内含 arm64-v8a 和 x86_64，最低 API 26 |
| Mac JVM 冒烟检查 | 样例媒体记录、校准点、本地 XML 和 protobuf 样例 | 集数解析、续播值、正负偏移、添加和删除校准点后的时间映射、XML 及 protobuf 解析通过 |
| Pixel 4 XL，Android 13，arm64-v8a | 《火线》S01E01、外挂 ASS、`ep830768`、自制双音轨 MKV 和本地 XML | 待复测覆盖安装、旧库及权限恢复、HEVC、续播、横竖屏、字幕和音轨、B 站缓存及 XML 回退、两种校准 |
| 平板模拟器，Android 16，arm64-v8a | 自制双音轨 MKV、ASS、本地 XML | 模拟器未成功启动。双栏、分屏、放大字体、输入法和 TalkBack 待运行验证 |
| x86_64 和 Android 8.0 | 自制样片 | 待运行验证 |

0.3.0 曾在同一台 Pixel 上验证《火线》1080p HEVC 硬解、外挂 ASS、拖动、全屏和续播。通过 `ep830768` 下载了 1,057 条弹幕，重开后读取了缓存。《火线》本地片长为 62:06，B 站来源为 55:30，仍需要用相符的来源或本地 XML 验证同步。
