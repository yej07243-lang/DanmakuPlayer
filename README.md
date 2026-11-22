# DanmakuPlayer

本项目以 **GPL-3.0-or-later** 开源，完整许可见 [LICENSE](LICENSE)，授权说明见 [COPYRIGHT](COPYRIGHT)。

这是原版 **0.1.0 构建 9** 的源码，不包含后来已撤销的新版本改动。

macOS 原生本地媒体库与 B 站弹幕播放器。支持普通投稿和可公开解析的番剧单集链接。当前为 **0.1.0 构建 9**，提供 Apple Silicon 直接下载版；该版已 Developer ID 签名、按所有者决定跳过 Apple 公证。

## 构建

需要 Apple Silicon Mac、macOS 14+、可用 Swift 5.9+ 工具链及 macOS SDK、Python 3。播放库已固定来源与 SHA-256：

本开源包不捆绑第三方播放二进制。`fetch-deps.py` 会从原版固定的上游地址下载依赖，并核对 SHA-256。完整验收素材不包含在此源码包内，可用项目脚本生成自制素材。

```sh
python3 scripts/fetch-deps.py
python3 scripts/build-local.py checks
python3 scripts/package.py
python3 scripts/dmg.py
```

`~/Library/Caches/DanmakuPlayerBuild/DanmakuPlayer.app` 是自包含应用。构建包放在非云同步缓存目录，避免文件提供器附加 Finder 元数据破坏签名。`dist/DanmakuPlayer-0.1.0-arm64.dmg` 可拖入 Applications。运行时不需要 Homebrew。构建机若设置 DEVELOPER_DIR，应指向实际存在的工具链。OpenGL 是 libmpv 官方 render API 使用的兼容路径，Apple 已标为 deprecated；迁移 Metal 是后续兼容工作，不宣称原生 HDR 输出。

## 使用

- 导入视频或文件夹；双击影片播放。单击显示详情。媒体库支持搜索、电影、剧集和继续观看。
- 在详情中“关联 B 站弹幕”，搜索片名或粘贴 BV／番剧单集链接，选择候选，再选分 P 并确认。
- 播放窗口的弹幕设置支持本地 XML、缓存刷新清除、屏蔽词与时间校准。“整体延后”为正时，弹幕在本地时间轴上更晚出现。分段点指定本地此刻对应的 B 站时间；整体延后仍会叠加。
- 空格暂停；左右方向键跳转五秒；F 全屏；D 切换弹幕。字幕与音轨菜单可切换轨道、加载 SRT/ASS/VTT，及调整字幕延迟。
- 文件夹递归扫描，读取同名 NFO、movie.nfo、tvshow.nfo；识别 `S01E01`；海报读取同名 JPG/PNG、poster.jpg/png、folder.jpg，否则尝试生成缩略图。无法生成缩略图不阻止播放。
- 文件移动或磁盘断开后，记录保留。右键选择“重新定位文件”；从媒体库移除不会删除原文件。

## 数据与恢复

正式数据：`~/Library/Application Support/DanmakuPlayer/`，包括 `library.sqlite`、WAL、缓存、缩略图和轮转诊断日志。升级前退出应用并备份整个目录，再替换应用。应用内部提供数据目录与日志导出入口。删除应用不会自动删除数据。

验证模式使用 `DANMAKU_DATA_DIR` 指定独立目录。测试库与正式库必须分开。不要在应用运行时仅复制 SQLite 主文件而遗漏 WAL。

B 站第三方接口可能访问受限，部分视频只返回部分弹幕；获取失败会报告真实错误，缓存或本地 XML 可继续播放。不自动选择未经用户确认的来源，不绕过登录或访问限制。

## 更新

首版手动更新。当前没有已发布的远端版本源或自动更新服务。退出播放器，备份数据，从可信渠道取得新版本，替换 Applications 内应用。不要把本地版本查看功能理解为联网更新检查。

安装与恢复见 `docs/INSTALL.md`；当前安装包、校验值与发布说明见 [GitHub Releases](https://github.com/yej07243-lang/DanmakuPlayer/releases)。实测证据及未完成项见 `docs/VERIFICATION.md` 和 `docs/ACCEPTANCE_CHECKLIST.md`；支持矩阵见 `docs/FORMATS.md`；依赖发布说明见 `docs/THIRD_PARTY.md`。

开源准备与分发说明见 [OPEN_SOURCE_RELEASE.md](OPEN_SOURCE_RELEASE.md)。源码授权完成不等于安装包已公证、依赖对应源码已逐字复现或三轮完整验收已完成。
