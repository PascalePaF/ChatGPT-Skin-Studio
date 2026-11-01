# 调研与架构

本项目采用 .NET 8 WPF 管理器与 C++17 本机画面合成。管理器识别官方安装身份及窗口；原生后台使用 Windows Graphics Capture、D3D11、DirectComposition、WIC、Media Foundation。后台按进程身份、创建时间和所属生命周期管理。

## 参考项目

调研时比较了以下开源项目的背景层、启动动画、主题组织及素材呈现方式：

- [Codex Dream Skin](https://github.com/Fei-Away/Codex-Dream-Skin)
- [Codex Dream Skin Manager](https://github.com/xxloocee/Codex-Dream-Skin-Manager)
- [Code-Codex](https://github.com/Rice-dog/code-codex)
- [Codex Dynamic Skin](https://github.com/CCDawn/Codex-Dynamic-Skin)
- [codex-skin](https://github.com/cixiangtao/codex-skin)
- [codex-background-theme](https://github.com/TonyNa-code/codex-background-theme)

参考项目各有自己的启动、调试或注入前提。本项目的管理器、渲染器和图标独立实现，没有将这些项目的源码或二进制作为捆绑依赖；具体区别见 THIRD-PARTY-NOTICES.txt。

cover 与 contain 对应不同取舍：前者可裁切以铺满，后者保持完整原画。产品提供独立选项，默认完整等比配黑色底毛玻璃，不把铺满误称为完整保留。

## Wallpaper 素材

参考了 [Wallpaper Engine 文件说明](https://help.wallpaperengine.io/en/functionality/editingwallpapers.html)、[命令行说明](https://help.wallpaperengine.io/en/functionality/cli.html)、[RePKG](https://github.com/notscuffed/repkg) 的格式实现及 [Wallpaper Engine Extractor](https://github.com/uKeps/wallpaper-engine-extractor) 的提取行为。

本项目自行实现只读 PKG/TEX、LZ4、DXT1/3/5 与受支持图像格式读取。场景沿引用选择高清主图；不捆绑第三方提取工具，不将静态主图等同完整场景合成。视频直接读取原文件；用户主动录制时才调用已安装引擎的独立窗口。

## 平台资料

- [Windows Graphics Capture](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture)
- [DirectComposition](https://learn.microsoft.com/en-us/windows/win32/directcomp/directcomposition-portal)
- [Media Foundation Sink Writer](https://learn.microsoft.com/en-us/windows/win32/medfound/tutorial--using-the-sink-writer-to-encode-video)
- [OpenAI 外观设置参考](https://learn.chatgpt.com/docs/reference/settings#appearance)

窗口像素合成不具备 DOM 语义。自动样本、后台存活和画面准备状态不能替代目标客户端的真实显示验证；当前限制见 KNOWN_ISSUES.md。
