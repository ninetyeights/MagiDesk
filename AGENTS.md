# MagiDesk — Agent Guide

PowerToys 风格的 Windows 桌面增强工具箱:一堆窗口/浏览器效率小工具,收在一个
Fluent 设计的托盘应用里。**C# / WPF / .NET 10 (`net10.0-windows`) + WPF-UI 4.2**
(Fluent + Mica,跟随系统明暗)。Per-Monitor-V2 DPI。UI 为简体中文。个人项目,持续演进。

> 完整的用户向介绍见 [README.md](README.md) / [README.zh-CN.md](README.zh-CN.md)。
> 本文件是给 **agent** 的:架构、约定、踩坑,别重复 README。

## 构建与协作规则（重要)

- **改完代码要 build 修错,但不要 run / 截图** —— 应用由用户自己运行。
- **build 到临时 OutDir**,避免锁住正在运行的 exe:
  ```bash
  dotnet build MagiDesk/MagiDesk.csproj -c Debug -p:BaseOutputPath=C:\Users\Chester\AppData\Local\Temp\claude\magidesk-build\ --nologo -v q
  ```
- 猜不出的 Win32 / 多屏 / DPI bug:**加状态变化日志**(写 `%TEMP%\magidesk.log`),让用户复现回传,别连续纯猜。
- git:功能开发**开新分支**(如 `feature/desktop-fences`),不影响现有功能;**未经用户确认不要 commit**。commit message **不加 Co-Authored-By / Claude 署名**(除非用户另行要求)。

## 架构

单进程、单实例(命名 Mutex + EventWaitHandle,第二次启动唤起已有窗口)。每个功能是一个
**Service**,在 `App.OnStartup` 里 `new` 出来并 `Start()`,`OnExit` 里 `Dispose()`。
Service 通常持有 UI 线程的 `Dispatcher`。跨功能访问走 `App.<Feature>` 静态属性
(如 `App.DesktopFences`、`App.QuickGrid`)。

- **`App.xaml.cs`** —— 启动装配 + 单实例 + 主题(启动前套用系统明暗,否则首帧深底浅字)+
  DPI 日志 + 低延迟 GC(`SustainedLowLatency`,防止 Gen2 阻塞回收超过 `LowLevelHooksTimeout` 把鼠标钩子静默卸掉)。
- **`MainWindow`** —— `ui:FluentWindow` + `ui:NavigationView`(左侧),每个功能一个 `Pages/*Page`。
  页面只翻转配置 / 调 Service,重活在 Service 里。
- **`Config/AppConfig.cs`** —— 单例 JSON 配置(`%APPDATA%\MagiDesk\config.json`),原子写 +
  `.bak` 回退 + 每日滚动备份。改字段后 `AppConfig.Current.Save()`;`AppConfig.Changed` 事件通知监听者。
- **`Native/`** —— P/Invoke 集中地(`NativeMethods` / `NativeConstants`),多屏全屏检测
  (`FullscreenDetector` / `FullscreenWatcher`)。

### 功能地图（`Features/`)

| 功能 | 目录 | 摘要 |
|---|---|---|
| 窗口拖动 | `AltDragger.cs` | Alt(可配)+拖动任意窗口移动;修饰键+右拖按象限缩放。全局 `WH_MOUSE_LL` 钩子。 |
| 边缘吸附 | `EdgeSnap/` | Alt 拖动时磁吸到显示器工作区/其他窗口边缘;挂在 AltDragger 循环上,Shift 时让位给 Zones。 |
| 窗口分区 | `Zones/` | FancyZones 式,Shift+拖动吸附;树状布局编辑器(切/并/删、整条分割线联动)、多布局、按屏分配、模板。 |
| 快速网格 | `QuickGrid/` | 全局热键(默认 Ctrl+Shift+G)弹 行×列 选择器,框选区域平铺当前窗口。 |
| 浏览器角标 | `BrowserBadges/` | 跟随浏览器窗口的每-profile 头像角标(Chrome/Edge/Brave/Vivaldi/Opera)。 |
| Profile Dock | `ProfileDock/` | 任务栏式浮动 profile 头像条,点击启动/聚焦/轮换;可做 AppBar 占位。 |
| 桌面盒子 | `DesktopFences/` | Fences/Coodesker 式桌面整理(见下)。默认关闭。 |
| 托盘 | `TrayService.cs` | 托盘图标 + 关闭到托盘;单一真正退出入口。 |
| 开机自启 | `StartupRegistration.cs` | 每用户 `HKCU\...\Run`。 |

## 关键约定与踩坑

- **WPF-UI 给所有窗口套 Mica backdrop** —— 想要透明/自绘的置顶窗口(如桌面盒子)必须逐窗关掉:
  `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE=38, DWMSBT_NONE=1)` + 在 WndProc 于
  `WM_ACTIVATE/WINDOWPOSCHANGED/SETTINGCHANGE/THEMECHANGED/DWMCOMPOSITIONCHANGED` 时重申 +
  延迟(空闲后)创建窗口,避开主题管理器那一次性 backdrop pass。
- **多屏 DPI:窗口位置用物理像素** —— WPF 的 `Left/Top`(DIP)按主屏 DPI 解释,在混合 DPI 下会漂。
  用 `SetWindowPos`/`GetWindowRect`(物理像素)存取位置。尺寸(`Width/Height`)保持 DIP。
- **透明窗口的黑发丝边** —— `AllowsTransparency` 窗口上,细边/抗锯齿边会在透明(黑)背衬上混出黑线。
  开 `UseLayoutRounding + SnapsToDevicePixels`,交互式改尺寸时把 `Left/Top/Width/Height` 取整。
- **COM interface 的数组参数默认按 SAFEARRAY 编组** —— 会内存损坏/`ExecutionEngineException`。
  给 `IShellFolder.GetUIObjectOf` 等的 `apidl` 加 `[In, MarshalAs(UnmanagedType.LPArray)]`。
- **Segoe MDL2 PUA 字形别直接打字面量** —— 用 `((char)0xE70E).ToString()` 之类从码点构造,避免被工具改坏。
- **鼠标钩子回调在 UI 线程** —— 别在里面做重活/同步枚举窗口;快照/枚举丢到 `Dispatcher.BeginInvoke` 或后台线程。
- **`UseWindowsForms=true`** 只为托盘的 `NotifyIcon`;csproj 里 `Using Remove` 掉了 `System.Drawing`/
  `System.Windows.Forms` 的隐式 using(和 WPF 的 `Brush`/`Rectangle` 冲突),托盘代码用全限定名。

## 桌面盒子设计（`Features/DesktopFences/`,架构 B)

架构 A(在真实图标后面放背板)失败:桌面 ListView 不透明,WPF/GDI 子窗口都不可见。
**架构 B**:启用时隐藏系统桌面图标(`DesktopIcons.ToggleShowIcons`),改用普通 WPF 窗口自绘"盒子"。

- `DesktopFenceService` —— 管理 `Dictionary<id, FenceBoxWindow>`;`Activate`(藏图标→渲染)、
  `Deactivate`(恢复)。所有增删改走显式 mutator。**纯展示变更**(布局/排序/透明度/颜色/只显示图标)
  走 `Relayout`(只重排单个盒子缓存,不重新枚举文件系统);**内容变更**才走 `Render` 重新枚举。
- 枚举在**后台线程**跑(`EnumerateFolder`/`Enumerate` 每项一次 shell 取名),带代际计数丢弃过期结果。
- `FenceBoxWindow` —— 固定标题栏[返回][名*][折叠][☰菜单];可调大小(左/右/下边 + 左下/右下角自绘手柄,
  带对其他盒子/工作区的边缘吸附);排序(名称/类型/大小/修改/创建,纯按键排、文件夹不单独分组);多选
  (Ctrl/Shift/全选)+ 复制/剪切/粘贴/删除/新建(`ShellOps` 走剪贴板 CF_HDROP + `SHFileOperation`);
  映射文件夹盒子(`FileSystemWatcher` 防抖实时刷新)。
- `ShellThumbnail` —— `IShellItemImageFactory.GetImage` 取真实缩略图(bottom-up DIB 要按 `biHeight` 翻转)。
- `ThumbnailLoader` —— 后台加载 + 按路径缓存(限并发),`Invalidate(path)` 处理原地替换。
- `ShellContextMenu` —— 单项原生右键菜单(`IContextMenu`);第三方扩展会拖慢,`Prewarm` 在后台线程预热 DLL。

## 品牌 / 资源（`MagiDesk/Assets/`)

- LOGO 方案 **Zone Snap**:圆角方内三块分区磁贴 + 右上角琥珀色四角星火花。
  渐变 `#2F53E6 → #7A45E6`(左上→右下对角),火花 `#FFB020`。
- `logo.svg` 是源;`app.ico`(多尺寸)/`logo-256.png` 由 GDI 脚本按同一渐变几何生成,改色要两边同步。
- 图标接入:csproj `<ApplicationIcon>`(exe)、`MainWindow` 的 `Icon` + `ui:TitleBar.Icon`、
  `TrayService` 从资源加载 `app.ico`(失败回退系统图标)。

## 测试

`MagiDesk.Tests`(通过 `InternalsVisibleTo` 访问内部类型)。
