# 验证记录

环境：Windows 11 26100 x64、.NET SDK 8.0.428、libmpv `v0.42.0-225-gc2a42f414`（client API 2.7）、
NVIDIA RTX 5060、屏幕 DPI 125%。界面验证使用 `DANMAKU_SOFTWARE_RENDER=1`（本机 D3D 输出异常时强制软件渲染）。

复现方式：先跑 `scripts/make-fixtures.ps1` 生成合成素材与夹具，然后按下面的命令执行。
所有报告都写在 `logs/`（已被 `.gitignore` 忽略）。

## 构建与静态检查

| 项 | 命令 | 结果 |
| --- | --- | --- |
| 构建 | `dotnet build -c Debug` | 0 警告 0 错误 |
| 校准数学 | `--check-calibration` | 23 项 PASS，退出码 0 |
| 定位迁移 | `--check-maintenance` | 7 项 PASS，退出码 0 |

`--check-calibration` 覆盖：无锚点恒等、单锚点、双锚点段内/跨段、首段前外推、`ToLocal`/`ToBili` 互逆、
乱序归一、重复/非单调锚点拒绝、时间解析（`mm:ss.s` / `h:mm:ss` / 纯秒 / 垃圾输入）。

## 播放链路

| 项 | 命令 | 关键结果 |
| --- | --- | --- |
| 出画出声 | `--selftest <视频> --report logs/a.json` | `videoDecoded`、`voConfigured yes`、`currentVo gpu`、`audioSamplerate 48000`、`currentAo wasapi`、`ok true` |
| 真片硬解 | 同上，1080p HEVC 片源 | `hwdecCurrent d3d11va`、`1920×1080` |
| 全屏 | `--selftest <视频> --fullscreen` | `fullscreen true`，窗口 rect 从 `(480,178)-(2080,1203)` 变成 `(0,0)-(2560,1440)`（整块显示器） |
| 播放位置 | `scripts/verify-resume.ps1` | 第一轮存 5.07 s，第二轮重开存 5.3 s，`Pass True`；状态栏显示「从 00:05 继续」 |
| 打不开的文件 | 播放一个非视频文件 | 顶部出现红色错误横幅，2 秒内判定（`idle-active` 且拿不到时长） |

## 弹幕

| 项 | 命令 | 关键结果 |
| --- | --- | --- |
| 解析与叠加 | `--selftest <视频> --danmaku media/selftest-danmaku.xml` | 12 条，模式分布 `{"1":7,"5":2,"4":2,"6":1}`，`sub-visibility yes`，截图含弹幕 |
| 偏移 | `--offset 1.0` / `--offset 8` | `sub-delay` 分别为 `1.000000` / `8.000000`；偏移 8 秒时同一时刻画面无弹幕 |
| 显示设置 | `--settings media/settings-demo.json`（字号 32、透明度 0.5、区域 0.5、速度 1.4、屏蔽词 `CAL-ONE`） | `danmakuFiltered 1`、`sub-text` 为空、`absentSubtitleHidden true` |
| 字体 | 同一夹具分别用微软雅黑与黑体 | mpv 日志 `fontselect: (Microsoft YaHei, …) -> MicrosoftYaHei` / `(SimHei, …) -> SimHei` |
| 分段校准 | 锚点 `[(0,0),(400,300)]`，夹具弹幕在 B 站 100 s / 350 s | `--at 100` 读到「样本一」，`--at 450` 读到「样本二」，`--at 200` 为空 |
| 热更新 | `--late-calibration` | 同一位置先读到空，补校准 1.5 s 内变成「样本二」，未跳转未重载视频 |
| 外挂字幕 | `--subtitle media/subtitle-fixture.srt` | `sid 1`（弹幕）/ `secondary-sid 2`（外挂字幕）、`secondary-sub-delay 0.2`、`secondary-sub-text` 读到字幕文本 |

## 媒体库

| 项 | 命令 | 关键结果 |
| --- | --- | --- |
| 扫描与解析 | `--scan <目录>` | `scanned 4 / stored 4`；NFO 覆盖标题/年份/剧情；`S01E01` 解析；时长 20 / 15 / 3726.98 s |
| 幂等 | 再扫一次同一目录 | 不产生重复条目（stored 仍为 4） |
| 缩略图 | 扫一个没有海报的目录 | 生成 `thumbnails/thumb-<hash>.jpg`（11.2 KB） |
| 继续观看 | 播放库内条目 9 秒后 `--library` | `continueWatching 1`、该条 `position 5.51` |
| 失效检测 | 把文件改名后 `--library` | `unavailable 1`，改回即恢复 |
| 数据目录隔离 | `DANMAKU_DATA_DIR=<临时目录>` | 库/缓存/缩略图/日志全部落在该目录，默认目录不受影响 |

## B 站

| 项 | 命令 | 关键结果 |
| --- | --- | --- |
| 番剧单集链接 | `--bili https://www.bilibili.com/bangumi/play/ep830768` | 解析出 cid，拉到 2732 条弹幕 |
| 整季 + 分集选择 | `--bili .../ss48164 --bili-pick 1` | 第 2 集，1757 条 |
| 普通视频 | `--bili https://www.bilibili.com/video/BV1ra4y1t7xY` | 解析出 cid 与标题，380 条 |
| 视频搜索 | `--bili-search "火线"` | 20 条候选（BV + 标题 + UP 主 + 时长） |
| 官方影视搜索 | `--bili-search "火线" --official` | 5 条（各季 season_id），首个候选能解析出各集 cid |

## 界面冒烟

`--selftest … --probe-ui` 会真的构造并加载六个窗口，报告里对应
`calibrationUiLoaded` / `settingsUiLoaded` / `biliUiLoaded` / `editUiLoaded` / `libraryUiLoaded` /
`appSettingsUiLoaded`，全部为 `true`。

截图脚本 `scripts/capture-window.ps1`：目标窗口在前台时用屏幕 BitBlt；抢不到前台时退化为 `PrintWindow`
并在输出里标注 `foreground NOT verified`。`PrintWindow` 下视频区域可能是空白，不能用来判断画面。

## 对照 macOS 清单补漏（2026-11-23）

本轮按 `DanmakuPlayer-macOS-Feature-Inventory.md` 逐项补入口并复验。验证时本机 WPF 的 D3D 输出异常
（窗口整片白），因此所有界面验证都带 `DANMAKU_SOFTWARE_RENDER=1` 强制软件渲染；这是环境问题，
不影响应用逻辑。

| 项 | 操作 | 结果 |
| --- | --- | --- |
| 剧集分组与卡片角标 | 扫描 `_testlibrary` 后打开媒体库，选「剧集」 | 组头按片名分组；「已关联弹幕」角标出现；截图 `docs/screenshots/library.png` |
| 详情时长与续播时间 | 选中条目 | 详情显示「S01E02 · 0:15 · 文件可用」，有进度时按钮显示「继续播放 · 时间（保存于 …）」 |
| 已看完状态 | 12 秒短片播到结尾 | `resume.json` 写入 `Watched: true`、`Position: 0`；媒体库显示「已看完」角标；重开从头播放 |
| 同名外挂字幕 | `movie.mp4` + `movie.srt` 直接打开 | 状态栏「已自动挂载外挂字幕：srt · subrip」，画面出现字幕，`secondary-sid` 指向 srt 轨道 |
| 弹幕设置三页 | UI Automation 打开「弹幕设置」→「时间校准」 | 点 +0.5 秒、在播放位置 00:12 打锚点 `→ 00:16.5`、整体延后填 2.5；列表显示锚点，`calibration.json` 同步保存 `OffsetSeconds: 2.5` |
| 隐藏文件跳过 | 3 个同名视频，其中 1 个隐藏文件、1 个在隐藏目录 | `--scan` 得到 `scanned 1 / stored 1`，只入库可见文件 |
| 无 ffprobe 兜底 | 把 `PATH` 去掉 ffmpeg/ffprobe 后 `--scan` | 片长仍为 12 秒（libmpv 无窗口探测），标题保持文件名解析结果 |
| 重新定位数据迁移 | `--check-maintenance` | 7 项 PASS：进度、校准、整体延后和两类边车都迁到新路径，旧路径清空 |
| 构建与自检 | `dotnet build`；`--selftest … --probe-ui` | 0 警告 0 错误；19 项检查全 PASS，六个窗口（含应用设置）都能构造 |
| B 站搜索 | `--bili-search "火线"` | 20 条候选，退出码 0 |

`scripts/probe-settings-ui.ps1` 是可重复运行的设置/校准界面探针（需要先用调试版打开一个视频）。

## 没有自动化验证的部分

- 物理扬声器是否出声（只验证到 WASAPI 输出链路建立、48 kHz 在跑）。
- 菜单、拖拽、全屏进出的手感；自动化只验证「窗口能构造、命令行链路正确」。
- 长时间播放（2 小时以上）的稳定性与内存占用。
