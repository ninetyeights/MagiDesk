# 桌面盒子（Desktop Fences）开发 TODO

> Coodesker / Stardock Fences 式桌面图标整理，集成进 MagiDesk。
> 可行性已验证：Win11 四原语（定位 ListView / 跨进程读 / 移动图标 / DefView 控制）全通，走**架构 A**（保留真图标 + 盒子底衬）。
> 详见 memory `desktop_fences_feasibility.md`。

图例：`[ ]` 待办 · `[~]` 进行中 · `[x]` 完成 · 🔴 高风险 · ⭐ 关键路径

---

## Phase 1 — 地基 + MVP 骨架

### M1.1 DesktopIcons 地基服务 ⭐
- [ ] 定位 `Progman → SHELLDLL_DefView → SysListView32`（含 WorkerW 变体遍历）
- [ ] 跨进程枚举图标：index / 名称 / 坐标（VirtualAllocEx + LVM_GETITEM* + ReadProcessMemory）
- [ ] 图标 → 文件路径映射（走 Shell IShellFolder 枚举，用于稳定标识）
- [ ] 移动图标 `LVM_SETITEMPOSITION32`
- [ ] 读/设自动排列（`GetWindowLong` 读 LVS_AUTOARRANGE + `WM_COMMAND 0x7041`）
- [ ] 网格对齐 `0x7042` / 显示图标 `0x7402`
- [ ] 单元：能枚举、能移动、能开关自动排列（打日志验证）

### M1.2 盒子数据模型 + 持久化
- [ ] `DesktopBox`：Id / 名称 / 屏幕区域(RECT) / 成员(文件路径列表) / 外观 / 所属显示器
- [ ] 存进 AppConfig（每显示器一组）
- [ ] 迁移/加载/保存（复用现有 config 原子写）

### M1.3 盒子底衬 overlay 渲染 🔴⭐
- [ ] **先做 Z 序 spike**：能否把窗口卡在“壁纸之上、图标之下”
  - [ ] 尝试 parenting 到 WorkerW / 插到 SysListView32 下方
  - [ ] 验证多显示器 + 高 DPI 下位置正确
  - [ ] ⚠️ 若走不通 → 评估退路（盒子画在图标上层但极低透明+点击穿透 / 或改架构 B）
- [ ] 半透明圆角盒子 + 标题栏渲染
- [ ] 每显示器一个 overlay，跟随分辨率/DPI 变化重排
- [ ] 从 config 恢复盒子 → 桌面出现盒子，重启还在

### M1.4 拖图标进盒子归位 ⭐
- [ ] 运行时关掉系统自动排列/网格对齐（退出恢复用户原设置）
- [ ] 检测图标被拖动（轮询坐标 / shell 通知）
- [ ] 落点命中某盒子区域 → 该图标写进盒子成员 + 排进盒内网格
- [ ] 盒子内自动布局（列数按盒子宽度算，图标依次排）
- [ ] 拖出盒子 → 移出成员

### M1.5 设置页 + 导航 + 双击隐藏
- [ ] `Pages/DesktopFencesPage`：启用开关 / 新建盒子 / 盒子列表管理
- [ ] MainWindow 导航项
- [ ] 双击桌面空白 → 一键隐藏/显示所有图标（`0x7402`）
- [ ] App.OnStartup 装配服务

> **Phase 1 里程碑（MVP）**：手动建盒子 → 拖图标进去 → 布局保存 → 双击隐藏图标。

---

## Phase 2 — 自动化

### M2.1 桌面变化监听
- [ ] `FileSystemWatcher` 监听 用户/公共 Desktop 文件夹
- [ ] `SHChangeNotify` 注册（覆盖非文件类图标变化）

### M2.2 规则引擎
- [ ] 规则模型：按 类型 / 名称关键词 / 扩展名 → 目标盒子
- [ ] 新图标出现 → 匹配规则 → 自动归盒
- [ ] 规则管理 UI

### M2.3 一键整理
- [ ] “整理一次”按钮：按规则重排全部现有图标

> **Phase 2 里程碑**：装新软件 / 下文件，图标自动进对应盒子。

---

## Phase 3 — 传送门 + 打磨

### M3.1 文件夹传送门（架构 B / 自绘）
- [ ] 盒子可绑定任意文件夹 → 枚举并显示该文件夹内容
- [ ] 盒内双击打开、右键 shell 菜单（IContextMenu）

### M3.2 外观 + 打磨
- [ ] 盒子外观自定义：透明度 / 颜色 / 圆角 / 标题栏
- [ ] 卷起（只留标题条）
- [ ] 盒内滚动 + 排序（名称/类型/时间）
- [ ] 拖拽动画、主题跟随、盒子锁定

> **Phase 3 里程碑**：主要能力对齐 Coodesker。

---

## 决策待定（你考虑）
- [ ] 是否立项 / 什么时候开工（当前还有 Zones/QuickGrid 的 15 项 backlog 并行）
- [ ] M1.3 Z 序若走不通，接受“盒子在图标上层+点击穿透”的折中，还是转架构 B（隐藏真图标全自绘，工作量大得多）？
- [ ] 是否引入 Vanara NuGet 库简化 Shell/ListView interop（省 boilerplate，但加依赖）
