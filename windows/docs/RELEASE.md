# 发布清单

## 发布前必须确认

- [ ] 仓库里没有个人路径、密钥、Cookie、Token：`git grep -n "Users\\\\"`、`git grep -ni "cookie\|secret\|token"`。
- [ ] `LICENSE`（GPL-3.0 全文）与 `THIRD-PARTY-NOTICES.md` 在仓库根目录，且打进了发布包。
- [ ] 弹幕缓存、日志、测试生成的报告不在仓库里（`.gitignore` 已忽略 `logs/`、`bin/`、`obj/`、`media/*.mp4`）。
- [ ] 演示素材是自己生成的（`scripts/make-fixtures.ps1`），不要用带版权的影视截图。
- [ ] 版本号与 `tag` 一致（`DanmakuPlayerWin.csproj` 里的 `<Version>`）。

## 版本号

当前 `<Version>0.1.0</Version>`。改版本时同时改：
- `DanmakuPlayerWin.csproj`
- `docs/RELEASE.md` 文末的发布记录
- tag 名：`v0.1.0`

## 构建发布包

```powershell
# 1) 自包含发布（不需要用户装 .NET 运行时）
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
  -o artifacts/publish

# 2) libmpv 单独跟着 exe 放（118 MB 的 DLL 不打进单文件）
copy ..\_deps\libmpv\libmpv-2.dll artifacts\publish\

# 3) 带上许可与说明
copy LICENSE,THIRD-PARTY-NOTICES.md,README.md artifacts\publish\
```

`artifacts/publish` 里应当有：`DanmakuPlayerWin.exe`、`libmpv-2.dll`、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`README.md`。

## 打包与校验

```powershell
Compress-Archive -Path artifacts\publish\* -DestinationPath artifacts\DanmakuPlayerWin-0.1.0-win-x64.zip -Force
Get-FileHash artifacts\DanmakuPlayerWin-0.1.0-win-x64.zip -Algorithm SHA256
```

把 SHA256 写进 Release 说明，附件上传 zip。

## 发布后核对

- [ ] 新机器（没装 .NET）解压 → 双击 → 媒体库能开。
- [ ] 导入一个文件夹 → 扫描出条目 → 双击能播放。
- [ ] 播放器里「B 站链接…」能拉到弹幕并出画。
- [ ] `--check-calibration` 退出码 0。
- [ ] `--check-maintenance` 退出码 0（重新定位的进度/校准/边车迁移）。
- [ ] Release 页面附件与 SHA256 对得上。

## GPL 合规提示

发布二进制时你需要同时提供对应源码的获取方式：

- 本项目源码：公开仓库地址（Release 说明里写出）。
- libmpv/FFmpeg：按 `THIRD-PARTY-NOTICES.md` 里的版本与提交号给出源码地址；
  如希望离线可用，可在 Release 里附一份源码归档或在说明里声明三年有效的书面提供承诺。
- 不要去掉 `LICENSE` 与 `THIRD-PARTY-NOTICES.md`。

## 发布记录

| 版本 | 日期 | 内容 |
| --- | --- | --- |
| 0.1.0 | 2026-11-23 | 首个 Windows 版本：媒体库、libmpv 播放、B 站弹幕与搜索、分段校准 |
