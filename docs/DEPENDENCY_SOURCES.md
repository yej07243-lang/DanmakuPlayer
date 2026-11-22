# 原版播放依赖源码

原版构建 9 使用从 IINA 1.5.1 提取的 GPL 播放库。`ThirdParty/Dependencies.lock.json` 固定 30 个组件的源码归档、版本和 SHA-256；libjxl 与 libplacebo 归档包含其固定子模块。完整源码包应与安装包放在同一发行页提供，不以网页链接代替实际归档。

本次核查发现：44 个 arm64 库中，43 个的可执行 `__TEXT,__text` 与固定上游依赖包相同。mpv 版本、归一化字符串、C 函数集合与补丁特征相同，但 Swift 编译器和 SDK 不同，机器码存在差异。该证据支持源码来源对应的推断，不构成对原始生产环境或逐字节可复现构建的证明。当前仍将原版 mpv 的精确生产源码对应标记为待确认；不能仅凭此推断宣布全部许可核查完成。

`ThirdParty/build-options.json` 保存逐组件编译选项，`ThirdParty/patches/` 保存 mpv 和 Rubber Band 补丁及其原始通知。独立编写的 `scripts/rebuild-original-deps.py` 用这些配置控制重建，不随包分发缺少独立 LICENSE 的上游构建脚本工程。原始源码中的版权、双许可证和测试资料许可均保持不变。

## 重建

需要 Apple Silicon Mac、Xcode/macOS SDK、Python 3、Meson、Ninja、CMake、pkg-config、Autoconf/Automake/Libtool 和 NASM。工具仅用于构建，运行应用不需要 Homebrew。源码归档放入 `sources/` 后执行：

```sh
python3 scripts/rebuild-original-deps.py --archives sources --work "$HOME/Library/Caches/DanmakuOriginalDependencies" --verify-only
python3 scripts/rebuild-original-deps.py --archives sources --work "$HOME/Library/Caches/DanmakuOriginalDependencies"
```

请给第二条命令指定不存在的工作目录。脚本核对全部源码 SHA-256、解包、应用补丁、按依赖顺序构建，保留每个组件日志及完整命令。FFmpeg 使用原版 `--enable-gpl` 配置，mpv 保留原版 LuaJIT、MuJS、ASS、多音轨和硬件解码选项；不切换到先前撤销的 LGPL 内核路线。更换 SDK 可能改变编译结果和最低系统兼容性，需要另行验证。

此重建脚本已检查源码完整性和选项覆盖，未声称在当前机器完成全部 30 个库的重新编译。原版应用代码保持不变，发行整理不包含重编播放功能。
