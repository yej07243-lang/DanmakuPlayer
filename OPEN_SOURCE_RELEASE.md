# 原版开源准备

项目所有者已同意原版应用源码以 GPL-3.0-or-later 开源。此目录从此前交付的 0.1.0 构建 9 源码归档恢复；Sources、Tests、Resources 和 Package.swift 均按原始校验值核对，未合入后续新版本改动。当前安装的应用没有被替换，也没有重新编译播放器。

LICENSE 保存 GPLv3 完整文本；COPYRIGHT 明确选择 GPLv3 或更新版本，并保留第三方原始许可。原始源码归档及文件校验记录在 ORIGINAL_SOURCE_MANIFEST.json 中。

本开源包不携带第三方 dylib。构建脚本保留原版固定下载地址与 SHA-256；依赖源码不能用任意相近版本替代。已整理 30 份依赖源码归档、补丁和构建选项；直接比较确认 44 个库中 43 个的可执行代码段与固定参考包相同，mpv 的精确生产源码对应仍待确认，见 [依赖源码说明](docs/DEPENDENCY_SOURCES.md)。

公开仓库：https://github.com/yej07243-lang/DanmakuPlayer 。仓库发布原版应用源码，并在 GitHub Releases 提供 Developer ID 签名、未公证的 Apple Silicon 安装包及依赖源码归档。项目所有者明确选择跳过公证；下载页必须保留首次启动拦截提示。三轮完整验收没有因开源授权而自动完成。

ARCHIVE-MANIFEST.json 和 ORIGINAL_SOURCE_MANIFEST.json 是历史原归档的溯源记录，不是当前公开工作树的完整性清单。许可证、README 及公开说明为本次开源整理更新；Git 提交记录当前内容。
