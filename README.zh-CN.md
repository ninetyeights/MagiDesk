# MagiDesk

[English](README.md) | **中文**

PowerToys 风格的 Windows 桌面工具箱 —— 把一组窗口与浏览器效率工具集成在一个
Fluent 风格的托盘应用里。

基于 **C# / WPF / .NET 10** 与 [WPF-UI](https://github.com/lepoco/wpfui)
（Fluent + Mica，浅色/深色跟随系统）。支持 Per-Monitor-v2 高 DPI。

> 个人项目，持续迭代中。界面为简体中文。

**[下载 v0.1.0-beta.1 测试版](https://github.com/ninetyeights/MagiDesk/releases/tag/v0.1.0-beta.1)** · [全部版本](https://github.com/ninetyeights/MagiDesk/releases) · [更新记录](CHANGELOG.md) · [反馈问题](https://github.com/ninetyeights/MagiDesk/issues)

## 下载与开始使用

首个版本为**公开测试版**，优先支持 Windows 11 x64。安装版和便携版均包含 .NET 运行时，无需另行安装。

| 文件 | 适用场景 |
| --- | --- |
| `win-x64-Setup-*.exe` | 常见 Intel / AMD Windows 电脑，安装使用 |
| `win-arm64-Setup-*.exe` | Windows ARM64 设备，仍需对应设备实机验证 |
| `win-x64-*.zip` / `win-arm64-*.zip` | 便携使用，完整解压后运行 `MagiDesk.exe` |

1. 在下载页面选择适合设备的文件。安装版默认按当前用户安装，无需管理员权限。
2. 首次启动**只开启窗口拖动**，其他工具在左侧对应页面按需开启。
3. 按住 **Alt + 鼠标左键拖动**移动窗口；**Alt + 鼠标右键拖动**调整窗口大小。
4. 默认关闭主窗口会收起到托盘；双击托盘图标重新打开，从托盘菜单选择「退出」结束运行。

安装版升级和卸载保留用户配置与真实桌面文件。升级前建议备份 `%APPDATA%\MagiDesk`。安装程序目前没有 Windows Authenticode 签名；应用内更新的清单签名属于独立的完整性验证机制。

详细安装、升级及配置恢复方法见[发布使用说明](docs/RELEASE.md)。


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
拖动分隔条（支持局部与全局切割、分割线转换）；支持多个命名布局、按显示器分配、内置模板，以及相邻
分区的边缘合并带。

### ⊞ 快速网格
全局热键（默认 **Ctrl+Shift+G**）在显示器上弹出行×列选择器；在格子上拖出一个矩形，
当前窗口即平铺到该范围。支持每显示器网格密度、一键居中、所有显示器视图。

### 🅑 浏览器微标
跟随每个浏览器窗口的悬浮 profile 头像微标，让你一眼分清众多 profile。多浏览器支持：
Chrome、Edge、Brave、Vivaldi、Opera。每个 profile 的头像、颜色、显隐均可自定义。

### ⌂ Dock
浏览器账号与普通应用可混排在集合、栏目中；通过“内容管理”维护集合，通过“项目库”选择应用和浏览器账号。支持运行中应用、窗口预览、固定应用、横向滚动／换行、图标大小，以及悬浮和任务栏占位模式。

### 桌面盒子
桌面文件归类、分页、映射目录、缩略图及临时唤出。统一桌面模式由应用显示桌面内容，并由恢复辅助进程保护系统图标恢复。首次使用请先了解[发布使用说明](docs/RELEASE.md)中的恢复方式。

## 其他

### 浏览器启动参数

在左侧「浏览器」下打开独立的「启动参数」页面，分别编辑 Chrome、Edge、Brave、Vivaldi、Opera 的附加参数，点击保存。参数适用于该浏览器的所有 profile，支持清空和命令示意预览；含空格的参数值使用双引号。

参数用于 Dock 发起的浏览器启动，聚焦已有窗口不会重新应用参数，部分参数需完全退出浏览器后才生效。为保留正确的账号选择，不允许自定义 `--profile-directory`、`--user-data-dir` 或单独的 `--`。设置参数后直接启动浏览器程序；清空后恢复优先使用已有 profile 快捷方式的行为。

- 系统托盘图标 + 关窗最小化到托盘；单实例（再次启动会把已有窗口弹到前台）。
- 可选开机自启（按用户 `HKCU\...\Run`）。
- JSON 配置位于 `%APPDATA%\MagiDesk\config.json`，采用原子写入、`.bak` 回退与每日
  滚动备份。
- 关于页面支持手动／自动检查 GitHub Releases 更新，确认后下载、校验并退出安装。自动检查默认关闭。
- 诊断日志位于 `%TEMP%\magidesk.log`。

## 更新与已知限制

在「关于」页面手动检查更新，自动检查默认关闭。下载和退出后的安装助手都会验证更新清单签名及安装包摘要。便携版通过应用内更新会转为安装版，原解压目录不会自动删除。

- 快速网格跨 DPI 恢复资源管理器时，可能出现短暂空白或过渡等待。
- Windows 10、ARM64、远程桌面重连和显示器热插拔尚未全面实机验收。
- 首版通过了 346 项无窗口测试以及依赖漏洞、凭据扫描；这些检查不能代替真实桌面和安装升级验证。

反馈问题时请提供应用版本、Windows 版本、显示器缩放和复现步骤。日志位于 `%TEMP%\magidesk.log`，分享前请检查其中的个人路径等信息。

## 构建与运行

开发构建需要 **.NET 10 SDK** 与 Windows。自包含发布包无需另装运行时；首版验收范围见[发布使用说明](docs/RELEASE.md)。

```powershell
dotnet run --project MagiDesk\MagiDesk.csproj
```

关闭窗口会最小化到托盘；从托盘菜单的 **退出** 才会真正退出。

### 测试

```powershell
dotnet run --project MagiDesk.Tests -- --headless   # 无窗口回归，不操作用户桌面
# --real-world 等交互测试会操作真实窗口，需单独安排
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

发布包构建：`powershell -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1`。

安装程序构建：在上述命令末尾添加 `-Installer`，需要 Inno Setup 6.3+。安装包及便携包均输出到 `publish`，附 SHA256 校验文件。

SDK 由 `global.json` 指定，NuGet 依赖使用已提交的锁文件。发布脚本会先运行安全扫描和无窗口测试。

推送与项目版本一致的标签后，GitHub Actions 自动构建 x64、ARM64 包并创建草稿 Release；对实际安装包在本机生成更新签名清单，验证后再公开发布。操作见[签名发布指南](docs/UPDATE-SIGNING.md)，私钥不上传到 GitHub。
