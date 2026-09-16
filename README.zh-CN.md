# MagiDesk

[English](README.md) | **中文**

未来需求、优先级与开发顺序见 [TODO 开发计划](TODO.md)。

PowerToys 风格的 Windows 桌面工具箱 —— 把一组窗口与浏览器效率工具集成在一个
Fluent 风格的托盘应用里。

基于 **C# / WPF / .NET 10** 与 [WPF-UI](https://github.com/lepoco/wpfui)
（Fluent + Mica，浅色/深色跟随系统）。支持 Per-Monitor-v2 高 DPI。

> 个人项目，持续迭代中。界面为简体中文。

## 工具

### 🖱️ 窗口拖动（AltSnap 风格）
按住修饰键（默认 **Alt**）在窗口**任意位置**拖动即可移动——针对任意可见窗口，不限
于当前焦点窗口。修饰键 + **右键拖动**为缩放，方向由光标位于窗口的哪个象限决定。移动
/ 缩放的修饰键可配置。

### 🧲 边缘吸附（磁吸）
按住拖动修饰键移动窗口时，窗口边缘会自动吸附到附近的**显示器工作区边缘**、**其他窗口
边缘**以及**对齐线**（同侧边 / 中线对齐）。基于可见边界（DWM 扩展边框）计算，吸附像素
级精准。吸附距离和目标可配置。（位于「窗口拖动」页内。）

### ▦ 窗口分区（FancyZones 风格）
拖动时按住 **Shift** 把窗口吸附到分区。树状布局编辑器支持切割 / 合并 / 删除分区、
拖动分隔条（整条网格线一起移动）；支持多个命名布局、按显示器分配、内置模板，以及相邻
分区的边缘合并带。

### ⊞ 快速网格
全局热键（默认 **Ctrl+Shift+G**）在显示器上弹出行×列选择器；在格子上拖出一个矩形，
当前窗口即平铺到该范围。支持每显示器网格密度、一键居中、所有显示器视图。

### 🅑 浏览器徽标
跟随每个浏览器窗口的悬浮 profile 头像徽标，让你一眼分清众多 profile。多浏览器支持：
Chrome、Edge、Brave、Vivaldi、Opera。每个 profile 的头像、颜色、显隐均可自定义。

### ⌂ Profile Dock
任务栏式的浮动 profile 头像条——点击即可启动、聚焦或轮询某个 profile 的窗口。支持命名
分组、单个 / 所有显示器，以及自由浮动或贴边停靠（像系统任务栏一样预留屏幕空间）。

## 其他

### 浏览器启动参数

在「浏览器徽标」页展开「浏览器启动参数」，分别编辑 Chrome、Edge、Brave、Vivaldi、Opera 的附加参数，点击保存。参数适用于该浏览器的所有 profile，支持清空和命令示意预览；含空格的参数值使用双引号。

参数用于 Dock 发起的浏览器启动，聚焦已有窗口不会重新应用参数，部分参数需完全退出浏览器后才生效。为保留正确的账号选择，不允许自定义 `--profile-directory`、`--user-data-dir` 或单独的 `--`。设置参数后直接启动浏览器程序；清空后恢复优先使用已有 profile 快捷方式的行为。

- 系统托盘图标 + 关窗最小化到托盘；单实例（再次启动会把已有窗口弹到前台）。
- 可选开机自启（按用户 `HKCU\...\Run`）。
- JSON 配置位于 `%APPDATA%\MagiDesk\config.json`，采用原子写入、`.bak` 回退与每日
  滚动备份。
- 诊断日志位于 `%TEMP%\magidesk.log`。

## 构建与运行

需要 **.NET 10 SDK** 与 Windows 10/11。

```powershell
dotnet run --project MagiDesk\MagiDesk.csproj
```

关闭窗口会最小化到托盘；从托盘菜单的 **退出** 才会真正退出。

### 测试

```powershell
dotnet run --project MagiDesk.Tests            # 单元测试（注入真实输入）
dotnet run --project MagiDesk.Tests -- --real-world   # 驱动真实 exe
```

## 项目结构

```
MagiDesk/
  Features/        # 每个工具一个目录（AltDragger、Zones、QuickGrid、
                   #   BrowserBadges、ProfileDock、EdgeSnap）+ TrayService 等
  Pages/           # WPF-UI 页面，每个工具一个 + 设置 / 关于
  Config/          # AppConfig（JSON 持久化）
  Native/          # P/Invoke 声明
  Hooks/           # 低级鼠标钩子
MagiDesk.Tests/    # 控制台集成测试
```
