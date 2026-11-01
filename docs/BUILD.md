# 构建与开发

## 环境

- Windows x64，PowerShell 7。
- .NET SDK 8.0.414（global.json 使用 latestPatch；运行时版本由项目文件固定）。
- Visual Studio 2022 / Build Tools 的“使用 C++ 的桌面开发”组件。
- 含 C++/WinRT 与 fxc.exe 的 Windows SDK。
- 生成安装包需要 Inno Setup 6；仅生成可运行目录时可跳过。

脚本通过 vswhere 或 VCToolsInstallDir 自动寻找 MSVC，通过系统安装位置寻找 Windows SDK。Inno 编译器支持当前用户安装、Program Files 安装或 PATH。

## 构建

在仓库根目录执行：

```powershell
pwsh -File ./tools/build.ps1
# 或只生成自包含程序
pwsh -File ./tools/build.ps1 -SkipInstaller
```

发布目录默认是 build/publish/win-x64，安装包和 SHA256SUMS.txt 在 dist。可用 -PublishDirectory 指定仓库 build 内的独立输出目录。发布时不包含调试符号，并将源码路径映射为通用路径。

原生编译会检查 VS、PS、Frost 三个着色器入口并将警告视为错误。脚本不安装程序、不更改桌面壁纸、不提交或推送代码。

## 检查

```powershell
dotnet run --project tests/OfflineSourceChecks/OfflineSourceChecks.csproj -c Release
dotnet run --project tests/CatalogChecks/CatalogChecks.csproj -c Release
dotnet run --project tests/LayoutChecks/LayoutChecks.csproj -c Release
dotnet run --project tests/ApplyChecks/ApplyChecks.csproj -c Release
pwsh -File tests/ReadabilityChecks.ps1
pwsh -File tests/NativeRecordingChecks.ps1
```

前四项覆盖素材格式、库与分组、离屏布局和应用任务生命周期。ReadabilityChecks 默认执行 18 组前景保真检查，使用 D3D 硬件设备或 WARP，不捕获用户窗口；不把这项检查当作所有文字对比度或真实客户端显示保证。

-ContrastExperiment 仅保留被撤回的文字增强实验的对比断言，不属于当前默认验收。它不能证明实际字体观感正确。NativeRecordingChecks 会生成本地测试图和短视频，输出到 build。

涉及窗口实际显示、主题按钮、输入穿透、DPI、全屏与缩放的行为，需另在目标客户端上人工核验。自动检查不能替代这些实机步骤。

## 诊断

程序支持 --diagnostics、--library-inventory、--inspect-media、--inspect-wallpaper、--extract-wallpaper。--verify-background 会临时合成并保存本机客户端原窗口与合成画面，仅用于明确授权的开发诊断；输出可能包含聊天内容，不应提交或上传。

build、dist、调试输出、本地协作配置与开发会话记录不纳入源码。Releases 单独提供 Windows 安装包及其校验值。
