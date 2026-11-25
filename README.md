# PBG Studio

轻量、免安装的 Windows 桌面工具，用于可视化编辑 LDCad 零件框配置（`.pbg`）。

## 使用

1. 从 [Releases](https://github.com/zizeyan/pbg-studio/releases) 下载 Windows 便携包，完整解压后双击 `PbgStudio.exe`。
2. 运行环境为 Windows 与 .NET Framework 4.7.2 或更高版本。
3. 首次启动会打开内置示例；也可点击“打开 .pbg”选择自己的配置。
4. 编辑分组、零件及属性后，点击“保存全部”。
5. 需要零件预览与缺失检查时，在“路径设置”中选择本地 LDraw 库根目录和 `LDView.exe`。

预览是由 LDView 生成的 PNG 缩略图。编辑 `.pbg` 本身不需要安装 LDView。

## 构建

在已安装 .NET Framework 4.7.2 或更高版本的 Windows 上运行 `Build.cmd`，
使用系统自带的 .NET Framework C# 编译器，输出 `PbgStudio.exe`。
源码采用 C# 5 / Windows Forms，无 NuGet 依赖。

## 许可

源码采用 **GPL-3.0-only**，完整条款见 [LICENSE](LICENSE)。
允许修改、二次开发与商业使用；对外分发本程序或修改版时须遵守 GPL，包括向接收者提供相应源码的义务。
合规的 GPL 商业使用不需要向作者购买商业授权。

对于 zizeyan 拥有相应权利的代码，可联系作者洽谈另一份商业授权。

LDCad、LDView 和 LDraw 是各自独立的第三方项目，本项目不打包分发它们；
它们的程序、零件库及其他资源适用各自的许可。本工具与这些项目无官方隶属关系。
