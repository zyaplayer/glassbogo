# GlassBogo 开发说明

更新：2026-10-29。本文是唯一的开发与使用说明，合并原架构、使用、插件、验收及玻璃实验文档。AGENTS.md 是开发约束；第三方许可单独保留，不属于重复说明。

## 1. 当前状态与整理结果

GlassBogo 是 Windows 桌面入口管理器，包含分类栏、应用/文件入口、工作台、日历和本地插件。**入口管理不移动、覆盖、改名或删除用户原文件。**

当前源码完整保留，包括未提交的交互改动和 V2 材质实验；本次没有 Git 回退。玻璃开发按用户要求暂缓，当前实验不视为已完成产品。

| 内容 | 实际状态 |
| --- | --- |
| 入口、分类、搜索、日历、插件、数据备份 | 已实现，有核心/Windows 服务及隔离界面验证 |
| 悬浮协调、共享选中层、弹窗复用、缓存、有限时长动画 | 当前产品路径已实现 |
| Acrylic / Accent / 分层对照、诊断、高光原型 | 独立 --glass-lab 实验已实现 |
| Liquid Glass 真实桌面观感 | **未通过**；用户反馈多个窗口仍像实色或无法判断 |
| V2 432 DIP 弹窗及完整 V2 动画组合 | 尚未接入产品 |
| 桌面折射、透镜放大、色散、融合 | 未实现，不以渐变或透明动画替代宣称 |
| 最终 V2 的两分钟待机、30 分钟耐久与完整实机证据 | 未完成，旧构建结果不能移用 |

整理后“桌面开发”仅保留 glassbogo 和 glassdesk-tools。三个独立旧目录（桌面日历、GlassDesk-流畅度预览、glassdesk-open-source）、生成物、旧文档和缓存移出工作目录，可恢复归档在：

    C:\Users\zhouyian\Desktop\GlassBogo-归档-20261029-152235

归档含整理前 82 个源码/配置文件及 SHA256 清单、Git 历史 bundle、旧文档、旧项目、测试报告与发布包。用户的 LOCALAPPDATA\GlassBogo 数据和已安装程序不在本次清理范围。“干净”指目录整洁，**不代表 Git 没有未提交改动**。

## 2. 目录与架构

    桌面开发/
    ├─ glassbogo/
    │  ├─ README.md                 本说明
    │  ├─ AGENTS.md                 开发约束
    │  ├─ GlassBogo.slnx            解决方案
    │  ├─ global.json              SDK 版本
    │  ├─ Directory.Build.props    公共版本/编译配置
    │  ├─ src/                     App、Core、PluginSdk
    │  ├─ tests/                   核心、Windows 服务、隔离宿主、故障插件
    │  ├─ samples/FocusPlugin/      示例插件源码
    │  ├─ scripts/                 构建、安装、测试、测量、打包
    │  ├─ tools/GlassBogo.Icon/     图标生成工具源码
    │  ├─ licenses/                第三方许可
    │  └─ THIRD-PARTY-NOTICES.txt   第三方声明
    └─ glassdesk-tools/
       ├─ dotnet/                  本项目使用的 SDK
       └─ dotnet-install.ps1       SDK 安装辅助脚本

.git 和编辑器/Git 配置保留。artifacts、bin、obj 可重新生成，不是项目源代码。不要提交数据库、真实入口路径、日志和私人截图。

| 模块/文件 | 职责 |
| --- | --- |
| GlassBogo.Core | 不可变记录、入口/日历服务、SQLite 事务、备份、迁移 |
| GlassBogo.App | WPF UI、原生窗口、托盘、快捷方式、图标、桌面发现、插件宿主 |
| GlassBogo.PluginSdk | 无 WPF 依赖的清单、命名管道协议、插件运行循环 |
| Design.cs / GlassTheme.xaml | 当前产品共享材质、主题、控件样式、按压反馈 |
| DockWindow.cs / HoverCoordinator.cs | 分类栏、分页缓存、悬浮状态机、菜单/拖拽/焦点保持 |
| SurfacePlacement.cs / ShellSurface.cs | 物理像素工作区、DPI 定位、显示器及桌面集成 |
| GlassMotion.cs | 当前弹窗内容透明度/位移动画、中断与清理 |
| GlassBackdrop.cs / GlassLab*.cs / GlassSurface.cs | V2 独立实验、诊断、兼容预设、局部高光 |

磁盘/开始菜单扫描在后台；快捷方式 COM 在专用 STA 线程解析。图标异步加载，缓存最多 512，同时读取最多 2 个；分类页面缓存最多 24 页。全部入口列表采用回收式虚拟化。插件按可见性启动/退出。关闭重型页面后有一次空闲内存整理，不在每次悬浮时强制 GC。没有持续桌面捕获或常驻重型 Shader。

## 3. 已实现的功能

### 入口、分类和桌面

- 首次勾选桌面入口，或拖入快捷方式、应用、文档、文件夹；文件夹由资源管理器打开。
- 分类新建、重命名、排序、移除；系统 inbox（待整理）不可删除。移除普通分类时入口回到待整理。
- 入口单分类归属、拖动换类与排序；重复添加定位已有入口。不同启动参数可构成不同入口。
- 分类显示最多四个预览图标，过多时横向滚动；展开四列三行、每页 12 项。
- 搜索、分类筛选、Ctrl/Shift 多选、批量调整/移除引用、失效目标重新定位。
- 方向键、Enter、Esc、PageUp/PageDown；小工作区滚动容纳内容；菜单、拖拽、焦点保持展开。
- 全屏时收起；支持显示器选择、自动收起、浅色/深色/系统主题、开机启动。
- 可选隐藏桌面图标，保存原状态并在退出时恢复；仅影响图标显示，不处理实际文件。
- 主程序单实例，重复启动打开已有工作台。关闭工作台或日历不等于退出；托盘提供退出与恢复桌面。

### 日历与工作台

- 月历、每周四时段课表、按日任务、任务编辑、拖动改期/时段、无时间任务排序。
- 日期、可选时间、完成状态、优先级、分类颜色、备注；跨日期搜索及完成/逾期/重要筛选。
- 中文速记支持“明天18点交报告”“下午三点半开会”“下周一第二节高数”；识别后核对再保存。
- 默认速记键 Ctrl+Alt+N，可调整；日历可置顶；工作台显示今日任务、入口和插件。
- 不包含云同步、订阅日历、重复任务、在线插件市场或全盘索引。

### 数据与恢复

- SQLite 事务存储，状态版本 1，单行 JSON 包含入口、分类、任务、设置和插件开关。
- 修改先复制集合，写入成功后替换内存状态并通知 UI；保存失败保留输入。
- 打开执行完整性检查；拒绝未来版本；损坏时保留原库并提供恢复入口。
- 每天首次修改前备份，保留最近 7 份；导入/恢复前另存恢复点。
- 恢复先构建新库，归档旧库和 sidecar，失败尝试回滚；桌面恢复记录独立于数据库。
- 导入旧 GlassDesk Data 和旧日历 calendar.json 仅读取源文件，支持去重与迁移统计；可导出日历或全部状态。
- 数据：%LOCALAPPDATA%\GlassBogo；安装：%LOCALAPPDATA%\Programs\GlassBogo；实验：%LOCALAPPDATA%\GlassBogo-Lab。
- 独立恢复命令：GlassBogo.exe --restore-desktop；GlassBogo.exe --recover-data "备份.json"。恢复数据前退出原实例。
- GLASSBOGO_DATA_DIR 可指定隔离测试数据目录；不可让自动测试指向真实数据。

## 4. 当前产品参数（不是 V2 目标）

| 项目 | 数值/行为 |
| --- | --- |
| 技术/版本 | .NET 10、WPF、Windows 原生接口；项目版本 0.1.1 |
| SDK/发布 | 10.0.401，rollForward=latestFeature；Release、win-x64、自包含、ReadyToRun |
| 默认绘制 | WPF 软件绘制；HardwareAcceleration=false，改后重启 |
| 分类栏 | 高 90 DIP、内边距 8、分类 80×72、横向步长 88、圆角 16 |
| 定位 | 工作区底部间距 12；最大宽约工作区 70%；收起高 20 DIP |
| 弹窗 | 472×396 DIP、圆角 22、内边距 16；与分类栏间隙 8 |
| 内容 | 标题 18 SemiBold、图标 44、名称 12；最小内容 400×362 |
| 缓存/分页 | 图标 512、读取并发 2、页面 24；四列三行，每页 12 |
| 悬浮等待 | 首次 100 ms、切换 35 ms、离开 240 ms、拖拽 350 ms |
| 共享选中层 | 90 ms 移动 |
| 展开/收起 | 内容 Y=8、Opacity=0 起始；展开 150 ms（点击 140）；收起 110 ms |
| 内容切换 | 90 ms 交叉淡化；旧页禁用键盘/鼠标；Popup HWND 复用 |
| 按压 | 缩放到 0.97，按下 70 ms，释放默认 180 ms 恢复 |
| 产品按钮高光 | MouseMove 驱动、最短间隔 16 ms、最大偏移 10 DIP，无常驻 Rendering 循环 |
| 默认设置 | Theme=system、Monitor=null、AutoHide=false、AutoStart=false、QuickShortcut=N |

现有产品 Tint：浅色 #18FFFFFF、深色 #300B1628；顶部高光：浅色 #A8FFFFFF、深色 #50FFFFFF。这些是现有值，**未通过 Liquid Glass 视觉验收**，不能误写为已按 V2 调整。

入口字段：Id、Name、Target、Kind（path/app/url）、Arguments、WorkingDirectory、CategoryId。路径归一化忽略大小写；参数保留大小写。分类名称 1–40 字；任务标题 ≤200 字；日期 yyyy-MM-dd、时间空或 HH:mm、优先级 low/normal/high；排序值须有限数，分类颜色为 #RRGGBB。

## 5. V2 实验、结论与下一阶段

实验命令：

    GlassBogo.exe --glass-lab
    GlassBogo.exe --glass-lab --compat

入口先于真实数据库、托盘和单实例初始化。普通模式 A=原生材质、B=材质+Tint/高光、C=不透明回退；另外有无 WPF 视觉树的 Win32 基准。兼容模式 A=官方 DWM 非分层，B=实验 Accent 非分层，C=实验 Accent 分层，D=不透明回退。

官方背景请求 DWMWA_SYSTEMBACKDROP_TYPE=3。Accent 使用未公开接口，明确标为兼容实验，不等于官方 Desktop Acrylic。C 分层配方无 WindowChrome、无框架扩展，支持同一 HWND 上“仅透明 / 恢复模糊”。不能把 AllowsTransparency=true 当作万能解决方案。

原型样本 432×320 DIP，内容圆角 22；浅色 Tint 初始 0.08（0.06–0.12）、深色 0.12（0.08–0.16）、高光 0.18（0.10–0.25）。Accent 自身另有原生 Tint：浅色 16/255、深色 24/255，不能简单相加推断最终观感。

GlassHighlightLayer 由指针事件驱动，离开后 150 ms 归位并释放时钟，隐藏/卸载停止，减少动画时不播放。透明关闭、高对比、节能、远程等条件回退。颜色/条纹参照板是普通自绘应用窗口，不截图、不修改壁纸，只帮助判断其他窗口内容合成，不能代替真实壁纸证据。

诊断 schemaVersion=2：记录实际系统构建/补丁、透明设置、电源、远程、DWM、普通/扩展窗口样式、分层标志、Owner、前台、DPI、WindowChrome、扩展参数、背景/合成目标、Opacity、绘制模式、HRESULT/BOOL、背景属性读回、回退原因和 DLL SHA256。API 接受与人工观察分开保存；**接口成功不证明玻璃像素成功**。

2026-10-29 用户反馈多个窗口像实色。当前未通过，根因未确定。背景 Acrylic 在失去激活时转为实色是待验证假设，不靠抢焦点掩盖问题。玻璃开发已暂缓。

继续开发的门槛：先拿到浅色、深色、高饱和壁纸上色彩参与与细节模糊、非激活状态及系统透明关闭/恢复的真实证据。若全部失败继续修合成，不加深蓝填充冒充玻璃。

通过后才接入拟定 V2 参数：弹窗宽 432、内边距 20、标题 17、图标 46、名称 12、每页仍 12；共享选中层 160 ms Cubic EaseOut；悬浮等待 100 ms，Y=8/Opacity=0/Scale=0.98 起始，165 ms 到位；按压 0.97、90 ms 恢复。**这些是计划值，不是现有产品实现。** 工作台/日历布局扩展暂停。

六项验收答案：实际 Acrylic 与桌面色彩参与未确认；薄 Tint/去全边框仅改变原型结构，不能保证成品观感；高光是自绘近似；静止时原型时钟会停止；真实折射、色散、透镜放大、融合未实现。

参考只读研究、无代码/Shader/品牌移植：

- 原 GlassDesk HEAD b1eb58c2d7205fca57e192e45d363872ced55624，阅读时存在本地修改；其捕获、分层与光学渲染管线未移植。文件指纹保存在归档旧文档中。
- [WPF-Liquid-Glass-Effect](https://github.com/dragosniamtu/WPF-Liquid-Glass-Effect/tree/5351f7c1854a241c04739cd485acb9946d7f370f)：Acrylic、桌面采集和像素 Shader。
- [WindowsLiquidGlass](https://github.com/JetComX/WindowsLiquidGlass/tree/e6d9885d4ff4e7b9fdf5814c6f9762efacf090a7)：D3D11、多级模糊/折射、图片背景。
- [shayann07/liquidglass](https://github.com/shayann07/liquidglass/tree/0cbd5e24058d2fb3fffe45b72cd07915d4dcea6f)：WPF 采样进程内控件并交给 Skia，不直接获取桌面。
- [微软 DWM 背景枚举](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)、[Acrylic 行为](https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic)。

## 6. 本地插件 SDK v1

插件是独立 EXE，宿主不加载任意插件 DLL；进程隔离用于稳定性，不是恶意代码沙箱。包内必须有 plugin.json，executable 为包内相对 EXE 路径，拒绝 .. 和链接目录。

    {"id":"glassbogo.focus","name":"专注小记","version":"1.0.0","apiVersion":1,"executable":"FocusPlugin.exe","capabilities":["card","page","command","entry-menu"],"subscriptions":["workspace.changed"]}

安装默认停用，查看来源/能力后启用。四类扩展：卡片、页面、命令、入口菜单。没有在线更新，卸载只处理插件包，不改入口、任务或原文件。

宿主通过 Plugin.exe --pipe <随机管道名> 启动；CurrentUserOnly 命名管道，UTF-8 每行一条 JSON。API 版本 1，请求 ID 原样返回，超时 5 秒；代码限制单行字符串长度 65,536 个字符，**不是严格的 UTF-8 64 KiB 字节上限**。

请求字段 apiVersion/id/method，可选 action/entryName。事件 method=event、action=workspace.changed，不发送全量任务/入口；主动调用入口菜单时才传选中入口名称。响应 id、view 或 error；view 有 title/text/actions，动作 id/label，最多显示 20 项。宿主统一自绘，不接受 HTML、脚本、任意 XAML 或嵌入窗口。

可见卡片订阅事件，250 ms 合并；卸载退订。取消、退出、故障、超时会终止会话并回收进程。示例 FocusPlugin 专注次数只存在当前会话。

## 7. 构建、运行与脚本

以下在 glassbogo 根目录执行。本机 PATH 的 dotnet 可能仅有运行时，应使用保留的 SDK：

    $sdk = '..\glassdesk-tools\dotnet\dotnet.exe'
    & $sdk build GlassBogo.slnx -c Release -warnaserror
    ./scripts/build.ps1 -DotNetPath $sdk
    # 需要运行时再执行：
    ./artifacts/publish/GlassBogo.exe

| 脚本 | 用途 |
| --- | --- |
| build.ps1 | 发布主程序到 artifacts/publish、示例插件到 artifacts/sample-plugin，附 README 和许可 |
| install.ps1 | 更新同一 LOCALAPPDATA/Programs/GlassBogo 和快捷方式；需退出已安装实例；数据不变 |
| test.ps1 | 核心、Windows 服务测试；生成示例/故障插件；可选隔离 UI smoke/endurance |
| test-glass-lab.ps1 | 时间戳原型构建与隔离材质测试，不自动打开主屏窗口 |
| new-glass-test-wallpapers.ps1 | 生成三张测试 PNG 到指定新目录，不设置系统壁纸 |
| measure-process.ps1 | 仅只读采样隔离 --qa 进程；PID、DesktopName、OutputDirectory 必填；默认 5 秒（2–60） |
| package-candidate.ps1 | 校验当前构建指纹与长测记录后打包；不同构建长测须单列说明和至少 180 秒当前回归 |
| package-glass-lab.ps1 | 只打包 P0 原型、校验报告 SHA，不当作产品视觉/性能通过 |

构建/测试支持 DotNetPath；安装支持 PublishDirectory。原型打包需 PublishDirectory、TestReportDirectory、WallpaperDirectory；产品打包需 TestReportDirectory，可选 EnduranceReportDirectory/FollowupChangeNote。归档旧报告只能对应原 DLL，不能套用于新构建。

## 8. 测试、性能与证据边界

    $sdk = '..\glassdesk-tools\dotnet\dotnet.exe'
    ./scripts/test.ps1 -DotNetPath $sdk
    ./scripts/test.ps1 -DesktopSmoke -DesktopSeconds 180 -DotNetPath $sdk
    ./scripts/test.ps1 -Endurance -DotNetPath $sdk
    ./scripts/test-glass-lab.ps1 -DotNetPath $sdk

DesktopSmoke 默认 10 秒应力阶段，可设 10–1800；Endurance 固定 1800 秒，另有 120 秒待机和结束回收。DesktopHarness 使用 CreateDesktop 和 CreateProcess.lpDesktop，不调用 SwitchDesktop。自动窗口操作必须在隔离桌面，真实材质由人工验收。

已完成 39 项核心/悬浮/定位和 10 项 Windows 服务测试，涵盖去重、原文件完整性、事务失败、备份恢复、迁移、日历、真实插件管道、崩溃/超时/取消。隔离 UI 另测分页、焦点、HWND 复用、动画中断、缓存、失效入口、模拟透明关闭/高对比、小视口。DPI 数学测试覆盖 100/125/150/200%、负坐标和任务栏工作区，不代表物理多屏已验收。

最新原型（2026-10-29）DLL SHA256：
3B5A87F5A39E724D17D6FAA525E0E7A109E6A483FF9FD10DAFEDAB2CF7DD1F42。
209 项隔离断言通过，包括 34 组基础宿主/激活诊断、兼容预设、透明/模糊切换和资源清理；**真实视觉仍未通过**。

历史性能按构建分别记录，单位 MiB；CPU 为进程 CPU 时间增量 / 墙钟 / 逻辑处理器数。数据规模为 500 个生成入口和 10,000 条任务，入口常用同一应用的不同参数，不等于 500 种图标冷加载。

| 构建标识 | 实测摘要 | 限制 |
| --- | --- | --- |
| D0DE7493689A… | 1800.18 秒、7967 切换/79 日历循环；120 秒待机 113.8 MiB、CPU 0.0065%；采样高点 151.7；结束 109.3；WPF 2→2；布局 P95 20.49 ms | V1 长测，不是当前 V2，不含系统 DWM |
| 578AB4680474… | 180.10 秒、807 切换/8 日历循环；18 秒待机 115.1 MiB、CPU 0.0036%；布局 P95 11.98 ms | 样式修订后的不同构建，未沿用上一构建长测 |
| F736E764E0FA… | 较早 P0 的 196 项断言；10.05 秒业务短测、46 切换；18 秒待机 112.6 MiB、CPU 0.01446%；入口+日历快照 150.8 MiB | 短样本，不是最新原型或 30 分钟指标 |

完整历史指纹：

    D0DE7493689AF2569FB29BB57A7AEBE1CDEC47FF85A81417480841DD92CC1460
    578AB4680474183A67B061FCAB37DA750DEEC69BA8B899026B2E026C941765F2
    F736E764E0FAEC3BB9B2C620731C4D2855F90E34DD69B0095FCB4128BE517CC3

V1 长测原生窗口另有 292 个只读样本：22→22、最多 23；USER 88→85、GDI 114→110。前期无效的跨桌面线程枚举零值已排除，正式样本用 EnumDesktopWindows。历史有一次短时 CPU 异常，复测未重现但根因未定。

目标：收起待机私有内存 ≤150 MiB、入口与日历同时使用尽量 ≤300 MiB、待机平均整机 CPU <0.5%。最终 V2 必须用同一交付构建重测，不能以编译或旧数值声称达标。布局 P95 仅测 ShowCategory→UpdateLayout，不含等待/DWM 呈现；采样高点不是瞬时峰值；离屏布局图不包含桌面合成，不证明玻璃。

尚需实机：三种壁纸、非激活、透明关闭、真实高对比配色、物理 DPI/多屏、OLE、跨间隙鼠标、全屏/休眠、首次展开/快速切换录屏和 DWM 系统开销。

## 9. 历史、约束与恢复

Git 提交：c3cba5c 独立应用基础；cea992d 保存失败处理和 Windows 验证；c3ffec2 界面样式/交互。随后未提交的 V1 加入悬浮状态机、定位数学、共享选中层、复用弹窗、交叉淡化与性能验证；V2 加入独立材质研究。本次只整理目录、统一说明并修改打包文档引用，保留 C#、XAML 与业务逻辑。

约束：不处理原文件，不提交真实数据，源码素材独立，磁盘/COM 不阻塞 UI，缓存有界，定时器/订阅随生命周期清理，自动 UI 测试必须隔离，性能实测，安装保持同一位置与恢复记录。

整理恢复：归档“整理前源码”是文件快照，source-manifest.json 可校验；glassbogo-history.bundle 保存 Git 历史；移动记录.json 记录原/新位置。先在新目录恢复和比对，避免覆盖后续工作。

第三方许可见 THIRD-PARTY-NOTICES.txt 与 licenses/；原创源码未被擅自赋予对外开源许可。自包含程序尚未代码签名，无在线更新服务。
