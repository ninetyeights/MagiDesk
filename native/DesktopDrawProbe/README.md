# 原生桌面绘制探针（第九版：空白菜单与退出恢复）

隐藏位置右键通过实际桌面 IShellView 的 SVGIO_BACKGROUND 获取系统背景菜单，
使用 IContextMenu/2/3 显示并转发菜单消息。不是隐藏文件菜单；可能呈现系统经典菜单样式。
菜单消息处理独立于隐藏实验，菜单打开期间到期也能移除隐藏处理。
页面把主程序 PID 传给辅助进程；Explorer 持有主程序的 SYNCHRONIZE 句柄，
约每 100 毫秒检查一次进程结束状态，正常退出或进程终止后停止隐藏并请求重绘。
这不是硬实时保证：Explorer 消息线程阻塞时需等其恢复。未传 owner PID 的命令行实验仍仅按计时恢复。
`ownerExited`、`backgroundMenus`、`menuError` 分别记录主进程结束、背景菜单打开次数和菜单错误。

第七版用户验证：视觉隐藏、框选和 Ctrl+A 排除有效，但方向键落到目标后焦点被清除。
第八版在方向键/Home/End 默认处理前，用 ListView 的空间邻接查询判断下一项。
只有下一项是隐藏目标时才接管，沿同一方向找下一个可见项；没有可见项则保留原焦点/选择。
普通移动仍交给 Explorer，Ctrl 移动仅改变焦点，Shift 扩展选择排除隐藏项。
`navigationSkips` 记录跳过次数，`navigationBoundary` 记录遇边界而保留原状态的次数。
此修改仍待用户运行验证；PageUp/PageDown 和按名称定位暂沿用旧的事后修正方式。

用户系统已确认使用 LVS_OWNERDATA，第五版因此返回错误 50，未安装隐藏处理。
默认保留已验证的视觉隐藏；“同时测试交互拦截”复选框默认关闭。
不勾选时不会安装选择/鼠标拦截，隐藏位置仍可交互，只观察消失与恢复。
第七版勾选后不再因虚拟列表直接拒绝：正常转发 LVN_ITEMCHANGED / LVN_ODSTATECHANGED，
再在同一调用中清除目标的选择/焦点/拖入高亮；键盘与菜单入口前额外复查。
与普通列表的事前否决不同，这是事后修正实验，可能仍有 Shell 内部路径或导航体验问题，
必须人工验证，不能将视觉成功视为交互成功。修正失败会停止隐藏并尝试立即移除处理。

目的：验证当前 Explorer 桌面是否发送可识别的单项绘制通知。旧的
IShellFolderView 探针已在用户系统上返回 E_NOINTERFACE，不能继续沿用。

本探针**尚未接入日常归类**。在桌面图标线程安装 WH_CALLWNDPROCRET 钩子，并在该线程
临时子类化图标控件的父窗口。仅当 CDDS_PREPAINT 原返回值为 CDRF_DODEFAULT 时，
追加 CDRF_NOTIFYITEMDRAW。通知测试仍不隐藏；另一个指定路径的实验仅对目标的
CDDS_ITEMPREPAINT 返回 CDRF_SKIPDEFAULT，其余图标正常绘制。
不移动、删除图标，不修改文件属性，不注册永久 Shell 扩展。

通知实验持续 5 秒，指定目标的隐藏与交互实验持续 15 秒；Explorer 线程上的定时器和窗口消息处理
负责移除父窗口与图标控件的子类化，辅助进程额外等待最多约 2.5 秒确认恢复。Explorer 若暂时无响应，
移除操作需等消息循环恢复，但截止时间之后的回调不再追加通知标志。
辅助进程退出也不会取消 Explorer 内部的恢复定时器。
为避免回调指向卸载后的代码，本实验将 DLL 固定在 Explorer 内存中直到该进程退出；
每次从独立临时副本加载，不锁住应用目录的 DLL。临时副本保留在
`%TEMP%/MagiDesk-DesktopDrawProbe-runs/`，Explorer 退出后可清理。
不要把“安装成功”或“收到通知”当作隐藏功能成功。

## 编译

要求 Windows x64、Visual Studio 2022 或 2026（含 Insiders）的“使用 C++ 的桌面开发”
（MSVC、Windows SDK、适用于 Windows 的 C++ CMake 工具）。脚本使用所选 VS 自带的 CMake。
这是独立实验项目，不加入主解决方案，避免影响已有 .NET 构建。

在仓库根目录执行：

```powershell
# 只检查工具，不编译：
.\native\DesktopDrawProbe\build.ps1 -CheckOnly

# 编译原生 DLL，并复制到指定的现有应用目录：
.\native\DesktopDrawProbe\build.ps1 -Destination 'MagiDesk/bin/Debug/net10.0-windows/'
```

脚本自动查找包含预览版的 VS，使用独立的临时构建目录；无需把编译器加入 PATH。
不传 Destination 时只生成 DLL 并打印路径，不复制。脚本不编译或启动主程序。
DLL 需要放在实际运行的 MagiDesk.exe 同目录（如果使用 Release，则调整目标目录）。
随后用户自行编译主项目、启动应用。在“桌面盒子设置 → 桌面项（调试）”点击
“测试原生桌面绘制接口”，在 5 秒期间让桌面图标可见，并移动鼠标到图标上。

日志：`%TEMP%/magidesk-desktop-draw-probe.log`。

- `callbacks=0`：没有观察到目标线程回调，不能判定桌面不支持自定义绘制。
- `callbacks>0, customDraw=0`：需继续核实通知接收窗口或其他绘制路径。
- `itemDraw>0`：仅证明能观察到单项通知；还未证明能安全隐藏、阻止点击或恢复。
- 原生错误码和退出码会保留，DLL 缺失会直接显示明确提示。

同时记录窗口处理后的返回值：`prePaint` / `postPaint` / `otherStage`
区分整体绘制阶段；`notifyItem` 表示 PREPAINT 返回值包含请求单项通知的标志，
`skipDefault` 表示包含跳过默认绘制的标志，`defaultDraw` 表示返回默认绘制（0）。
`prePaintFlags` 是全部 PREPAINT 返回值的按位或，不能当作某一次的完整返回值。
若 `prePaint>0, notifyItem=0`，说明观察期间最终返回值没有请求单项通知；
若已请求但 `itemDraw=0`，则还需检查其他绘制路径，不能直接认定支持隐藏。
主程序和 DLL 都需要重新编译；使用版本化导出，避免新旧结构大小不一致导致越界。

第三版增加 `attempted` / `installed` / `restored` / `installError` / `modified`。
正常应为 `installed=1, restored=1, installError=0`，且 `modified>0` 表示实际追加过通知请求。
只有恢复确认成功且收到单项通知才报告通过。未安装或未确认恢复以退出码 1 报告，
不会误报通过。接口导出为 `ObserveDesktopV9`，必须同步更新主程序和 DLL。

## 单图标视觉隐藏实验

保留通知测试，完整路径输入框旁的按钮为“临时隐藏指定图标（15 秒）”。
以下选择清理和交互拦截说明仅适用于勾选交互实验时。
填写用户桌面或公共桌面下现有文件/文件夹的完整路径（可以粘贴系统“复制文件地址”的带引号结果）。
让图标所在桌面区域可见，点击按钮，观察目标是否消失、约 15 秒后是否恢复。
开始前仅清除目标已有的选择/焦点/拖入高亮，到期不恢复旧选择，避免覆盖用户在实验期间的选择。
隐藏位置的鼠标按键事件被拦截，选择/焦点变化通过 LVN_ITEMCHANGING 否决，批量设置状态仅排除目标。
重命名开始通知也被拦截。恢复时须移除两处子类化才确认成功。
LVS_OWNERDATA 不发送可否决的选择通知，本版本改用状态变化后的同步修正，不再返回退出码 8。
正在编辑或鼠标拖动时拒绝开始（退出码 9）。旧 DLL 明确提示升级（退出码 7）。
外部文件拖入、触摸、辅助功能调用和所有 Shell 命令路径尚未覆盖，仍不接入日常归类。

Explorer 线程通过 IShellWindows → IShellBrowser → IShellView → IFolderView 获取桌面视图，
并核对视图窗口与当前图标控件父窗口一致。每次单项绘制均以当前索引取得 PIDL，
通过所属 IShellFolder 生成 IShellItem，再取 SIGDN_FILESYSPATH 与目标完整路径比较。
不缓存图标序号、不用显示名称匹配。无法解析身份的项目保持显示，记录 identityError。
到期移除临时处理并请求擦除重绘。`matched` 是路径匹配次数，`suppressed` 是跳过绘制次数；
`suppressed>0` 只证明拦截已执行，实际视觉消失/恢复仍需用户确认。
退出码 6 表示没有拦截到目标。日志同时记录文件存在性和前后属性，不写入文件或属性。

## 交互回归用例（待用户运行验证）

以下用例仅在勾选交互实验且支持该控件类型时执行；默认视觉模式不要操作隐藏位置。
建议使用不涉及重要操作的测试快捷方式；每轮最多 15 秒，可重复测试：

- 单击、双击、右击隐藏位置：目标不打开、不弹出目标菜单；旁边可见图标仍可操作。
- 从空白处框选跨过隐藏位置、Ctrl+A、Shift 多选：目标不进入选择；其他图标可正常选择。
- 方向键、Home/End、键入名称选择：目标不应获得焦点；检查导航是否被卡在目标前。
- 实验前选中目标：开始后目标取消选择，Enter 不应打开它。
- 从隐藏位置尝试起拖：不能拖出目标；从旁边开始的框选在隐藏位置松手应正常结束。
- 15 秒后图标重新可见、可选、可打开；不再拦截原位置。
- 对 `.lnk`、普通文件、文件夹分别复测；检查日志 `restored=1, installError=0`。

`interactionReady=1` 仅表示已安装交互处理。`blockedMouse`、`blockedSelection`、`blockedRename`
记录各类拦截次数；零表示未观察到该类拦截，不能算作通过。进程退出码 0 也不代表上述用例全通过。

第七版日志：`ownerData` 标记虚拟列表，`stateEvents` 是收到的单项/范围状态变化通知次数，
`correctedSelection` 是成功清除目标状态的次数，`correctionFailures` 是清除后仍有状态的失败次数。
退出码 10 表示修正失败并已请求恢复；还应检查 `restored=1` 确认移除成功。
单项变化仅检查该项目，范围变化和命令入口检查选中/焦点项目；不缓存目标索引。

本版重点人工用例：勾选后仍能隐藏；框选/Ctrl+A 后不包含目标，其他项目保持可选；
方向键跨过原位置时检查是否停滞；在实验前先选中目标，实验后按 Enter 不应打开它；
15 秒后目标恢复正常。每轮单独测试一组，回传日志以区分事前拦截和事后修正。
以上用例尚未由 agent 运行，不使用删除等破坏性操作验证。

## 后续验证门槛

第九版回归（待运行）：隐藏原位置右击应出现桌面背景菜单，取消菜单后其他图标操作正常；
菜单保持打开超过实验截止时间时目标应恢复；重新测试并在开始后立刻从托盘退出，
目标应在主进程结束后迅速恢复，无需等满 15 秒。重启应用后检查日志 `ownerExited=1, restored=1`。
主进程异常终止的恢复机制已加入，但尚未实测，不等同于已验证崩溃恢复。

第八版导航回归（待运行）：
- 上下左右分别从相邻可见项跨过隐藏项：焦点/高亮移到同方向下一可见项。
- 隐藏项位于该方向末端：原可见项保持焦点/选择，不变成全未选中。
- 长按方向键、Home/End：不落到隐藏项，也不失去焦点。
- Ctrl+方向键：已有选择保持，仅焦点跨过隐藏项；Shift+方向键：扩选排除隐藏项。
- 重测框选、Ctrl+A 和到期恢复，确认已有成功行为不回退。

1. 将原生图标的稳定身份与完整路径对应，不能仅按名称或易变下标匹配。
2. 只拦截一个明确目标的绘制，并在实验到期后自动撤销；验证原位置、文件与属性不变。
3. 验证隐藏后不响应点击、双击、框选、键盘选中、Ctrl+A、拖拽与右键操作。
4. 验证自动排列、F5、文件重命名、显示器/DPI 切换、Explorer 重启及主程序异常退出。
5. 以上通过后才接入自动归类，并考虑撤掉软件内的“桌面”盒子。

参考：
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw
- https://learn.microsoft.com/en-us/windows/win32/controls/nm-customdraw-list-view
- https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-setwindowsubclass
- https://learn.microsoft.com/en-us/windows/win32/controls/lvn-odstatechanged
- https://learn.microsoft.com/en-us/windows/win32/controls/lvm-setitemstate
- https://learn.microsoft.com/en-us/windows/win32/controls/lvm-getnextitem
- https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellview-getitemobject
