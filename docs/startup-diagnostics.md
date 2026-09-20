# 启动卡顿诊断

诊断随正常启动自动开启，持续 90 秒后停止，不需要设置环境变量或启动调试器。没有更改桌面接管或图片加载策略。

日志写入 `%TEMP%\magidesk.log`（轮转备份为 `.1`），筛选 `STARTUP-TRACE`。仅显式设置 MAGIDESK_DIAGNOSTICS=1 时才同步写到 Visual Studio 的调试输出，默认只写非阻塞文件日志。每行包含时间、进程 ID、启动以来毫秒数和线程 ID；`begin/end` 的 `id` 对应同一操作，`ms` 为耗时。

重点查看：

- `fences.activation-queued` 到 `fences.activate.begin`：等待主窗口和主题初始化后的空闲调度。
- `desktop.membership`、`desktop.enumeration`、`folder.enumeration`：后台目录与文件身份读取。
- `desktop.ui-ready`、`folder.ui-ready`：后台完成到界面线程开始接收的等待。
- `lease.process-start`、`lease.wait-ready`：恢复辅助进程启动和就绪等待；辅助进程使用独立 PID，`lease.helper` 对应它，`guard.ready` 表示就绪。
- `shell.desktop-settings`、`surface.render-ui`、`items.render-ui`：桌面 Shell 接口、窗口创建与布局。
- `icon.worker/slot/load`、`thumbnail.worker/slot/load`：线程池排队、并发槽等待及加载耗时。
- `shell.image-create/get/convert`：创建 Shell 对象、图像提取、位图转换。
- `image.ui-delivery`：图片可用后等待界面回调的时间。
- `ui-stall`：界面心跳间隔超过 500 毫秒；`sample` 为每秒的线程池、托管内存和未完成阶段摘要。停在调试器断点时也会出现大间隔，不能当成应用本身卡顿。

诊断只记录计数、阶段及进程内匿名键，不记录文件名和路径。日志采用非阻塞有界队列，繁忙时可能丢记录；未见 end 不单独证明死锁。90 秒外没有新诊断记录属于正常情况。完整控件树仅在显式设置 `MAGIDESK_DIAGNOSTICS=1` 时输出，避免淹没启动日志。

2026-09-19 已有日志：桌面快照 238 ms，453 项元数据 263 ms，首批内容提交 497 ms；部分图标首次交付耗时 4476～5257 ms。现有记录还无法区分图像提取与界面排队。界面线程同步等待恢复进程（最长 5 秒）是重点怀疑位置，需本轮新增的分段日志确认。

复现：退出并重新启动应用，等待桌面和盒子内容显示完毕后反馈。不要运行测试来测启动速度；测试日志中的临时目录和配置访问受限信息不代表主应用启动情况。


后续复现（主进程 37380）：453 项目录枚举 239 ms，结果等待界面回调 13992 ms；图片界面交付等待约 5～7.5 秒；恢复进程就绪等待仅 255 ms，排除其为本次主要瓶颈。内容完成回调改为显式 Loaded 优先级，避免继承激活时 ApplicationIdle；逐行调试器输出改为显式启用。新增两个真实 Dispatcher 无窗口测试，完整 264 项连续三轮通过。实际启动改善仍待再次运行验证。

再次复现（主进程 49952）：文件夹内容回调等待从 13992 ms 降到 15 ms，桌面回调 58 ms。图片首批交付仍排队约 5355～5364 ms，174 条图片回调中位等待 649 ms。图像交付改用与内容一致的 Loaded 优先级（只赋值图片，不在回调中读文件），保留取消检查。新增排队/取消两个无窗口调度测试，完整 266 项连续三轮通过；实际图片改善待新启动日志确认。

Render 深层诊断：render.breakdown 将一次 Render 调度内 window.measure/arrange、canvas.measure/arrange、canvas.tile-layout 等耗时分列，数值包含嵌套调用，不能简单相加。render.capabilities 和 window.render-state 记录渲染等级、透明窗口大小/DPI及渲染模式；window.visible-tiles 记录实际生成数量。渲染等级不是当前窗口确实使用 GPU 的证明；布局很短而 Render 很长只说明耗时位于未覆盖的后续 WPF/合成路径，仍不能证明文字阴影是原因。本轮保留全部视觉效果。

文字阴影对照：经用户确认，DesktopRenderExperiment.DisableDesktopLabelShadow 临时设为 true，仅移除桌面文件名的 DropShadowEffect，不写入用户配置。启动日志 render.experiment 和 shadowLabels=0 用于确认运行的是对照版本。对照基线进程 10040：Render 1649/959 ms，布局各约 3 ms。测试后将开关恢复为 false。

阴影对照结果（49552）：shadowLabels=0 已确认，两次 Render 为 1820/1802 ms，布局仍约 2 ms；关闭阴影未消除卡顿，不能据此判定它是主因。已恢复 DisableDesktopLabelShadow=false。后续应定位 WPF 合成/透明窗口绘制路径，不能把 Render 优先级调度耗时直接等同 GPU 执行时间。

调用栈采样（39052，采样前 10 秒 UI 原生线程 18616）：MediaContext.FireLoadedPendingCallbacks 累计约 3.33 秒；其中 RemoveUnloadedCallback → DispatcherOperation.Abort → Monitor.Enter/WaitHelper 约 1.53 秒，图标 Unloaded → CancellationTokenSource.ExecuteCallbackHandlers 约 1.08 秒。数据为抽样线程时间估计，包含等待，不能当作纯 CPU 耗时；另观察到 Debugger.NotifyOfCrossThreadDependency。已将图标订阅取消改为 CancelAsync 后异步释放，并移除图像交付 DispatcherOperation 的令牌注册，由交付回调检查取消状态避免旧图回填。阴影保留。完整 272 项测试连续三轮通过。

重建源头检查：OnDpiChanged 排队的 Relayout 无条件 force 重建，RenderItems 中 SetItems 又无条件 ClearItems。已按最终 DPI 合并并跳过过时请求；VirtualItemCanvas 按内容记录与模板键保留未变化控件，排序只改变位置，变化项单独替换，真实 DPI/显示模式变化继续重建。新增 5 项真实 WPF 控件无窗口回归（复用、排序、单项变化、模板变化、清空重建），成功构建后的 277 项连续三轮通过。运行效果待验收。

复用修正验收（40708）：本轮日志未再出现秒级 Render，最高已记录 Render 为 73 ms；图片回调中位等待 62 ms、最大 187 ms，首批约启动后 3.17 秒交付。前 10 秒 UI 线程采样中 FireLoadedPendingCallbacks 约 77.7 ms，EventRouteFactory.Push 约 8.5 ms（上轮约 1879.7 ms），RenderMessageHandlerCore 累计约 259.3 ms。说明本轮控件重建相关的长等待已明显消退；启动初期仍有 1.403 秒和 636 ms 心跳间隔，不能宣称完全无卡顿。


## 鼠标启动卡顿：独立钩子线程

鼠标钩子从 WPF UI 线程迁移到带消息循环的专用 STA 线程。安装、重装、卸载及拖动状态保持同线程；重装检查也在该线程执行，拖动期间不重装。
窗口移动/缩放使用异步 SetWindowPos；最大化还原异步请求，后续鼠标事件读取还原后的矩形，不等待目标窗口。吸附枚举仍在 UI 上构建独立列表，结果投递回钩子线程，并检查拖动代次。Zones 继续异步更新 UI，退出后丢弃迟到通知。

`MouseHook: started` 记录线程启动；`MouseHook: slow callback` 记录超过 50ms 的回调。不逐条记录鼠标移动。
自动测试使用假安装/卸载回调，不安装真实全局钩子、不注入输入，覆盖 UI 不泵消息时的独立执行、顺序、重复退出、安装失败清理、排队异常恢复和退出后丢弃任务。

真实体验需用户重启后验证：启动期间移动鼠标、Alt 左拖/右拖、最大化窗口拖动、多屏边缘吸附、Shift 分区及快速松开。自动测试不能证明真实输入卡顿已消除。


### 残余卡顿诊断

增加 dock.catalog / monitors / groups / window-create / profiles / show / restore-position / initial-state 分段。暂不改加载顺序（包括已有重复目录读取），先确认实际耗时。
启动前 90 秒每秒记录 gc-pause：进程累计 GC 暂停时间及与上次采样的差值；第一次差值是进程启动以来累计值。lastIndex/lastGeneration 指最近完成的 GC，不代表区间内只有一次回收。采样不能给出每次暂停的精确起止时间。
钩子线程每 200ms 检查消息循环心跳，仅间隔超过 250ms 时记录 mouse-hook.loop-delay；90 秒后停止。这个指标是线程调度/消息循环延迟，不是鼠标硬件事件到回调的端到端延迟。不会逐条输出鼠标移动日志。


### 鼠标事件分段延迟

启动 90 秒内 `mouse-hook.event-delay` 将事件系统时间戳到回调入口的近似差值（arrivalMs）、本应用处理（ownMs）和 CallNextHookEx 返回等待（nextMs）分开记录。任一阶段至少 50ms 才输出，每 250ms 最多一条；被吞掉的事件不调用后续钩子。时间戳处理 32 位回绕，注入事件及未来时间戳记 unknown。arrivalMs 不包含硬件采样前的延迟；nextMs 包含后续钩子、原生处理和期间线程调度，不能单凭这个值归因某款软件。限频日志不能用于统计所有慢事件数量。
