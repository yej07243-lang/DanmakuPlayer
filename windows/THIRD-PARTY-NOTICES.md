# 第三方组件与许可

本项目以 **GPL-3.0-or-later** 发布（因为随包分发的 libmpv 构建启用了 GPL 组件）。
下面是随发布包一起分发的第三方组件、版本、许可和对应源码的位置。

## libmpv（`libmpv-2.dll`）

| 项 | 值 |
| --- | --- |
| 版本 | `mpv v0.42.0-225-gc2a42f414`，client API 2.7 |
| 构建来源 | [zhongfly/mpv-winbuild](https://github.com/zhongfly/mpv-winbuild) 的 release `2026-11-22-c2a42f414f`，文件 `mpv-dev-x86_64-20261122-git-c2a42f414f.7z` |
| 许可 | GPL（该构建启用了 x264 等 GPL 组件，整体按 GPL 分发） |
| 对应源码 | mpv：<https://github.com/mpv-player/mpv>（提交 `c2a42f414f`）；构建脚本与补丁：<https://github.com/zhongfly/mpv-winbuild> |
| 说明 | 该 DLL 同时包含 FFmpeg、libass、libplacebo 等库，见下 |

## FFmpeg

| 项 | 值 |
| --- | --- |
| 版本 | `N-129222-g329157f58`（由 `libmpv-2.dll` 内嵌字符串确认） |
| 许可 | 该构建含 GPL 组件（如 libx264），按 GPL 分发 |
| 源码 | <https://github.com/FFmpeg/FFmpeg>（提交 `329157f58`） |

## libass

| 项 | 值 |
| --- | --- |
| 许可 | ISC |
| 源码 | <https://github.com/libass/libass> |

## libplacebo / libplacebo 依赖

| 项 | 值 |
| --- | --- |
| 许可 | LGPL-2.1-or-later |
| 源码 | <https://github.com/haasn/libplacebo> |

## x264

| 项 | 值 |
| --- | --- |
| 许可 | GPL-2.0-or-later |
| 源码 | <https://code.videolan.org/videolan/x264> |

## .NET 8 / WPF

| 项 | 值 |
| --- | --- |
| 许可 | MIT |
| 源码 | <https://github.com/dotnet/runtime>、<https://github.com/dotnet/wpf> |

## Microsoft.Data.Sqlite / SQLite

| 项 | 值 |
| --- | --- |
| 许可 | MIT（Microsoft.Data.Sqlite） / Public Domain（SQLite） |
| 源码 | <https://github.com/dotnet/efcore>、<https://sqlite.org> |

## 获取对应源码

- 本项目源码：随仓库提供（GPL-3.0-or-later）。
- libmpv/FFmpeg 的对应源码：按上面的仓库存放地址获取，版本号与提交号已在上表列出。
  如果你需要离线归档，建议按 `docs/RELEASE.md` 的说明在发布 Release 时附带源码包或镜像链接。
- 发布包中的 `LICENSE` 是 GPL-3.0 全文；本项目以 "or later" 方式授权。
