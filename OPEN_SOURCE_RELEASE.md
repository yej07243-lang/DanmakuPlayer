# 原版开源准备

项目所有者已同意原版应用源码以 GPL-3.0-or-later 开源。此目录从此前交付的 0.1.0 构建 9 源码归档恢复；Sources、Tests、Resources 和 Package.swift 均按原始校验值核对，未合入后续新版本改动。当前安装的应用没有被替换，也没有重新编译播放器。

LICENSE 保存 GPLv3 完整文本；COPYRIGHT 明确选择 GPLv3 或更新版本，并保留第三方原始许可。原始源码归档及文件校验记录在 ORIGINAL_SOURCE_MANIFEST.json 中。

本开源包不携带第三方 dylib。构建脚本保留原版固定下载地址与 SHA-256；依赖源码不能用任意相近版本替代。原版 IINA 1.5.1 二进制对应的完整源码、补丁和构建记录仍需核对，尤其此前发现版本对照不一致的五个库，见 docs/THIRD_PARTY.md。

公开仓库：https://github.com/yej07243-lang/DanmakuPlayer 。本仓库仅发布原版应用源码；不提供未经发布核验的安装包。安装包的正式签名、公证核验及三轮完整验收没有因开源授权而自动完成，不能标记为全部发布条件已通过。

ARCHIVE-MANIFEST.json 和 ORIGINAL_SOURCE_MANIFEST.json 是历史原归档的溯源记录，不是当前公开工作树的完整性清单。许可证、README 及公开说明为本次开源整理更新；Git 提交记录当前内容。
