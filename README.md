# GlassBogo

一个轻盈的 Windows 桌面入口工作台：用底部分类整理应用、文档和文件夹，悬浮展开，点击打开。液态玻璃界面、原生日历、本地插件。

**入口不是原文件。** 添加、移动分类或移除入口，不会移动、复制或删除原文件。文件夹通过 Windows 资源管理器打开。

0.1.1 重做玻璃材质与界面层次：统一浅深色控件、侧边导航、细滚动条及矢量图标，移除叠加的原生边框；加入悬停抬起、按压回弹和分类切换过渡。动画只在交互期间运行，并跟随系统减少动画设置。

## 使用

- 点击桌面 **GlassBogo** 快捷方式启动；再次点击会打开已有实例的工作台。
- 首次使用勾选要收录的桌面入口，也可以拖入文件、选择已安装应用或添加文件夹入口。
- 点击底部“＋ 分类”命名分类；拖动方块排序，拖动入口调整归属。每个入口只有一个分类。
- 悬浮分类展开，点击入口打开。右键可以重命名分类、移除分类或移除入口。移除分类后，其入口进入“待整理”。
- 分类内按页展示，每页 12 项；滚轮或 PageUp / PageDown 翻页，方向键选择、Enter 打开、Esc 收起。全部入口列表支持搜索、多选、批量调整分类和失效目标重新定位。
- 重复添加会定位已有入口并保留其原分类；拖动已有入口才会调整归属。
- 默认使用低内存控件绘制，玻璃背景仍由 Windows 合成；设置中可切换硬件加速，重启生效。
- 设置中可隐藏 Windows 桌面图标。默认不隐藏，正常退出会恢复；崩溃后重新启动也会尝试恢复。
- 日历支持月历、每周课表、任务分类、搜索和中文速记，例如“明天18点交报告”“下周一第二节高数”。识别后需核对并保存。
- 托盘菜单提供工作台、日历、恢复桌面和退出。全屏应用期间底部栏自动收起。

应用不接管系统文件夹打开方式，不包含文件处理、云同步、在线插件市场或全盘索引。

## 数据与恢复

数据位于 `%LOCALAPPDATA%\GlassBogo`，程序位于 `%LOCALAPPDATA%\Programs\GlassBogo`。更新程序不覆盖数据。

- SQLite 事务保存；每天首次修改前生成 JSON 备份，保留最近 7 份。
- 导入、恢复前生成额外恢复点；旧 GlassDesk 与旧日历只读取，不共同写入数据。
- 设置中可导入旧 GlassDesk 的 `Data` 文件夹、旧日历的 `calendar.json`，也可导出与恢复 GlassBogo 备份。
- 数据库损坏时不会清空覆盖。启动错误窗口可选择 JSON 备份恢复，原数据库先归档。
- 独立恢复命令：`GlassBogo.exe --restore-desktop`。
- 数据恢复命令：`GlassBogo.exe --recover-data "备份.json"`。恢复时须退出其他 GlassBogo 实例。
- 测试可设置 `GLASSBOGO_DATA_DIR` 使用隔离目录，勿指向真实数据。

隐藏桌面图标依赖 Windows Explorer 的桌面窗口。Explorer 尚未就绪时保留恢复记录，稍后重试；本应用不替换 Windows Shell。

## 开发

需要 Windows 11 x64 与 .NET 10 SDK，源码使用 `global.json` 固定 SDK 起点。独立编写的 C#/WPF 实现，不含 GlassDesk 代码或素材。

```powershell
dotnet build GlassBogo.slnx -c Release
dotnet run --project tests/GlassBogo.Tests -c Release
./scripts/build.ps1
./scripts/install.ps1
```

脚本支持 `-DotNetPath` 指定 SDK 可执行文件。`scripts/test.ps1` 运行核心与原生服务测试；加 `-DesktopSmoke` 在独立 Windows 桌面执行界面测试，不切换用户桌面。长测显式使用 `-Endurance`。

资料：[使用指南](docs/USAGE.md) · [架构](docs/ARCHITECTURE.md) · [插件 SDK](docs/PLUGINS.md) · [验收记录](docs/VALIDATION.md)。

## 来源与许可

GlassBogo 的新增源码与素材由本项目独立实现。目前未提供原创代码的对外开源许可；公开仓库不等于授予任意使用或分发许可。第三方组件继续遵循各自许可，见 `THIRD-PARTY-NOTICES.txt` 和 `licenses/`。
