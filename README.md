# ChatGPT Skin Studio

Windows 本地皮肤管理器，为 ChatGPT / Codex 桌面窗口提供图片、视频背景与启动过渡。

**当前公开版本：V1.1.4 预览版。** 项目采用窗口画面合成，没有官方插件接口；不同客户端版本、灰阶图片和特殊面板可能存在显示差异，请先阅读[已知问题](docs/KNOWN_ISSUES.md)。

[下载 Windows x64 安装包](https://github.com/PascalePaF/ChatGPT-Skin-Studio/releases/download/v1.1.4/ChatGptSkinStudio-1.1.4-Windows-x64-Setup.exe) · [版本与校验文件](https://github.com/PascalePaF/ChatGPT-Skin-Studio/releases/tag/v1.1.4) · [使用说明](使用说明.txt) · [从源码构建](docs/BUILD.md)

## 功能

- 全窗口背景：本地图片、静音循环视频，以及内置动态背景；默认完整等比显示，并提供深色/浅色毛玻璃补边、纯色留边、裁切和拉伸选项。
- Wallpaper Engine 本地识别：发现 Steam / Workshop / 手动目录，读取原图和现成视频；支持场景主图离线提取，普通预览与应用无需运行引擎。
- 场景生成离线视频：主动调用已安装的 Wallpaper Engine 渲染并保存静音 MP4，之后直接使用本地副本。静态提取不包含粒子、光效或脚本。
- 壁纸库：搜索、来源筛选、分组、多选移动、移出与恢复、后台自动扫描；收起预览后展示全宽图库。
- 启动过渡：内置动画或本地 MP4，支持时长、预览、开关、托盘和登录启动。
- 管理器外观：日间、夜间、万圣夜；内置原创“万圣夜庄园”插画与轻微动态效果。
- 客户端明暗切换入口：通过官方外观设置控件操作，属于实验性功能，当前版本完整按钮流程尚未完成实机验收。
- 保留官方普通启动入口，不修改官方安装文件，不要求调试端口或 API Key。客户端最小化时，应用操作会先恢复已验证的窗口。

## 安装与开始

1. 从 [Releases](https://github.com/PascalePaF/ChatGPT-Skin-Studio/releases) 下载 Windows x64 安装包。安装包自带 .NET 运行时。
2. 从原来的官方入口打开 ChatGPT / Codex 桌面客户端，再运行 Skin Studio。
3. 在壁纸库选择素材，展开预览，点击“应用全局皮肤”。普通预览与应用不会切换 Windows 桌面壁纸。
4. 使用“暂停皮肤”暂时停止，或“恢复原样”停止并取消自动恢复。关闭管理器会回收其所属后台；“收起到托盘”会继续运行。

源码及运行目标是 Windows x64，最低 Windows 10 1903；实机主要验证于 Windows 11。macOS、Linux、HDR、多显示器 DPI 与其他客户端版本未作完整兼容承诺。

## Wallpaper 素材说明

图片和现成视频直接读取原文件，不以低清封面代替。场景项目沿引用提取可定位的高清主图，结果是静态素材，不等于作者在 Wallpaper Engine 中的完整实时场景；纯 3D、程序化或没有高清主图的项目可能无法静态提取。

需要完整动态效果时，可主动生成一段 10 / 20 / 30 秒的视频。只有生成阶段需要已安装的 Wallpaper Engine；生成结束后使用本地副本。循环不保证首尾无缝，不包含声音或鼠标交互。

用户下载的壁纸、视频和场景不包含在本仓库或安装包中，其权利归各自作者。内置庄园插画为本项目生成的素材，实际尺寸为 1672×941，生成说明保存在 assets。

## 开发

WPF / .NET 8 管理器，C++17 原生渲染器；使用 Windows Graphics Capture、D3D11、DirectComposition、WIC 和 Media Foundation。

构建环境：PowerShell 7、.NET SDK 8.0.414、Visual Studio C++ 桌面工具、包含 C++/WinRT 的 Windows SDK；安装包另需 Inno Setup 6。

```powershell
pwsh -File ./tools/build.ps1
```

输出安装包位于 dist。仅发布可运行目录可加 `-SkipInstaller`。详细依赖与检查命令见[构建说明](docs/BUILD.md)。

## 反馈与参与

请在 [Issues](https://github.com/PascalePaF/ChatGPT-Skin-Studio/issues) 描述系统和客户端版本、图片/视频/场景类型、显示模式、复现步骤。分享截图或日志前请遮盖聊天内容、个人路径和账号信息。不需要上传第三方完整壁纸包来报告普通显示问题。

已知限制与未完成验收项见[已知问题](docs/KNOWN_ISSUES.md)，公开版本变化见[更新记录](CHANGELOG.md)，参考资料见[调研与架构](docs/RESEARCH.md)。

## 许可

[MIT](LICENSE)。运行时许可与参考项目说明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。这是独立的第三方项目，与 OpenAI、Valve 或 Wallpaper Engine 作者没有隶属关系。
