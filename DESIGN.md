---
name: DanmakuPlayer
description: 原生 macOS 中文媒体库与弹幕播放器
colors:
  playback-stage: "#000000"
typography:
  headline:
    fontFamily: "system-ui"
    fontWeight: 700
  title:
    fontFamily: "system-ui"
    fontWeight: 700
  body:
    fontFamily: "system-ui"
  label:
    fontFamily: "system-ui"
  danmaku:
    fontFamily: "system-ui"
    fontSize: "24pt"
    fontWeight: 600
rounded:
  poster: "10pt"
spacing:
  card-content: "8pt"
  form: "16pt"
  grid-column: "20pt"
  content-inset: "24pt"
components:
  poster-artwork:
    rounded: "{rounded.poster}"
  playback-stage:
    backgroundColor: "{colors.playback-stage}"
  source-sheet:
    padding: "{spacing.content-inset}"
    width: "760pt"
    height: "540pt"
---

# Design System: DanmakuPlayer

## Overview

**Creative North Star: "原生媒体库"**

原生中文 macOS 媒体库：系统侧栏、海报网格和详情检查器承载浏览，独立深色窗口承载播放。沿用 SwiftUI 与 AppKit 的控件、字体、菜单和窗口行为，让影片资料与海报形成视觉重心。

记录当前源码实现；数值单位为 macOS 逻辑点。`system-ui` 是系统字体的可移植名称，实际字体、字号、控件外观与焦点反馈由 macOS 决定。未将系统动态颜色或语义字号伪装成固定值。

**Key Characteristics:**
- 系统外观与语义颜色；播放窗口固定深色。
- 海报比例稳定，网格随窗口宽度调整列数。
- 中文操作标签、SF Symbols 和原生键盘操作。

## Colors

### Primary

选择描边使用 `Color.accentColor`，保留用户系统强调色。海报续播进度单独使用 SwiftUI `.teal`，不是全局选择色。主要操作使用系统 `.borderedProminent`，不另设品牌色。

### Neutral

媒体库跟随系统明暗外观，次要资料使用 `.secondary`，无图海报使用 `NSColor.controlBackgroundColor`。不可用文件标识使用 `.regularMaterial`，播放控制栏使用 `.bar`。播放舞台使用 frontmatter 的黑色；播放窗口整体固定深色。错误使用系统 `.red` 并显示文字。弹幕颜色来自内容数据，不属于界面调色板。

**The System Color Rule.** 系统语义颜色跟随 macOS；仅播放舞台使用固定黑色。

## Typography

字体均为系统字体。媒体库标题使用 `.largeTitle.bold()`；分组、详情与弹幕设置标题使用 `.title2.bold()`；海报标题及小节使用 `.headline`；年份、来源、集数等元数据使用 `.caption` 与次要颜色。时间读数使用 `.monospacedDigit()`，不替换为另一套等宽字体。海报标题最多两行，来源标题最多三行。

弹幕使用系统半粗体，默认字号见 frontmatter，可调范围 16–40pt；这项用户设置不改变应用文字层级。其余语义字号与行高未固定，不在文档中推算。

## Layout

媒体库为 `NavigationSplitView` 加详情区 `HSplitView`。侧栏最小/理想/最大宽度为 170/190/240pt；主内容最小宽度 460pt；选中影片才显示宽 280pt、外侧 padding 20pt 的检查器。

海报网格使用 adaptive 156–220pt 列宽、20pt 列间距、24pt 行间距；内容横向 inset 为 24pt。海报宽高比为 2:3，文字与图片间距 8pt。剧集按片名分组。窗口缩放改变网格列数；没有网页断点或移动端布局。

媒体库内容最小 960×640pt，默认窗口 1220×800pt。播放窗口初始 contentRect 1100×700pt，AppKit `minSize` 为 720×480pt。来源 sheet 为 760×540pt，编辑 sheet 宽 480pt；弹幕 popover 内容宽 380pt 并 padding 20pt。

## Elevation & Depth

界面主要依靠原生分栏、分隔线与系统材质组织层次，没有自定义卡片阴影。弹幕文字使用黑色投影（radius 1pt、x/y 各 1pt）保持视频上可读；该投影不推广到控件。原生菜单、sheet 和 popover 的阴影交给 macOS。

## Shapes

仅海报裁切明确指定 10pt 圆角；选中海报使用 3pt 强调色描边。控件、输入框及窗口圆角沿用系统，不从海报圆角推导另一套组件形状。海报没有额外外框容器。

## Components

- **导航与搜索：** 原生侧栏 `List(selection:)`，原生 `.searchable` 和排序 `Picker`。底部展示片数，扫描时展示小型进度指示；重新扫描与导入按钮在扫描期间禁用。
- **主要操作：** 导入、播放和确认关联使用 `.borderedProminent`；检查器播放按钮采用 `.large` control size。工具栏使用带 SF Symbols 的 `Label`；其他按钮保留原生样式。
- **海报：** 单击选择、双击或 Return 播放，提供上下文菜单。文件不可用显示图标与文字；续播显示进度，已看完与弹幕来源使用图标。海报组合可访问元素，以标题和集数命名并提供播放 action。
- **表单与来源选择：** 原生 `Form`、`TextField`、`Toggle`、`Slider`、`Stepper`、双列 `List` 与文件面板。搜索忙碌时禁用重复操作；未选择来源时禁用确认。空结果使用 `ContentUnavailableView`，错误显示文字；编辑 sheet 提供取消/默认动作快捷键。
- **播放控制：** 进度与音量为原生 Slider；播放、前后跳转、弹幕开关、设置、字幕/音轨、播放列表和全屏均有可访问名称。Space 播放/暂停，方向键跳转 5 秒，D 切换弹幕，F 全屏。错误行可选择文本且可关闭。
- **弹幕：** Canvas 不接收点击且对辅助功能隐藏；滚动弹幕生命周期 8 秒，顶部/底部固定弹幕 4 秒。TimelineView 最小间隔 1/60 秒，暂停、关闭或无弹幕时停止时间线更新。源码没有自定义 Reduced Motion 分支；不宣称相关验收已通过。

源码依据：`Sources/PlayerUI/{LibraryView,PlayerView,SourceSheet,DanmakuPlayerApp,PlayerSession}.swift` 与 `Sources/PlayerCore/Models.swift`。本次为源码扫描记录；原生外观、VoiceOver、焦点路径、窗口适配和三轮完整实测仍以运行证据为准。`.impeccable/design.json` 的小型 HTML 片段仅供文档面板展示结构，浏览器系统控件不复刻 AppKit，不构成视觉证明。未运行 HTML detector；原生项目不适用。

## Do's and Don'ts

### Do:
- Do 保留原生侧栏、菜单、表单、文件面板与窗口行为。
- Do 用标题、年份、集数、来源和可恢复错误帮助用户作决定。
- Do 保留键盘入口、图标按钮的可访问名称和非颜色状态标识。

### Don't:
- Don't 添加多余解释小字、装饰渐变或营销式构图。
- Don't 将系统语义颜色冻结成任意十六进制值。
- Don't 把源码检查或浏览器示意当作原生运行验收。
