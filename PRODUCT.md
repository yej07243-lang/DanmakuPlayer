# DanmakuPlayer

## Product
原生 macOS 本地媒体库播放器。用户导入自己的电影与剧集，确认 B 站普通投稿作为弹幕来源，校准时间后播放。媒体文件始终留在原处。

## Platform
macOS 14+，Apple Silicon 先行；Windows 后续独立实现。

## Stack
SwiftUI + AppKit、libmpv、SQLite；本地运行，无自建后端。独立应用包包含播放依赖。

## Confirmed experience
侧栏、海报网格、详情页和独立播放窗口。中文 UI，不加多余解释小字。原生菜单、快捷键、文件选择与错误恢复。资料来自文件、NFO、同目录图片或视频缩略图。

## Acceptance
三轮完整实测，包含真实弹幕、兼容异常、发布包及长时间播放。没有实测的项目不得记为通过。Developer ID 与公证不足则不得宣称完成公开发布。
