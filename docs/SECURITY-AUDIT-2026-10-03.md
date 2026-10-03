# MagiDesk 安全审核报告

审核日期：2026-10-03。分支：`codex/security-audit`。范围为当前工作树（包含已有未提交功能），不是仅审核 HEAD。未提交代码，未运行 MagiDesk、安装程序或桌面探针，未截图。

## 1. 结论与范围

本轮发现并修复 3 处代码防护缺口，另完成系统 DLL 加载、依赖维护和敏感文件忽略规则 3 项加固。未确认可直接远程利用的 Critical/High 漏洞；**更新包缺少独立发布者签名属于 High 级供应链风险，仍未解决**。用户已明确选择本轮保留更新流程，将签名验证列为发布前待办。

项目为 C# / WPF / .NET 10 Windows 桌面应用，WPF-UI 提供界面，WinForms 仅用于托盘；另有一个可选 C++17/MSVC 桌面实验探针、PowerShell 构建脚本和 Inno Setup 安装脚本。两个 csproj，NuGet 为唯一运行时包管理器。直接包 2 个，去重间接包 1 个。没有 npm/pip/Go/Rust/Java/iOS 依赖清单，没有浏览器扩展，没有仓库 CI 工作流。

审核方式：对源码文件逐文件进行模式扫描与入口定位，人工审查安全敏感入口及调用链，包括更新、进程启动、文件操作、配置、Shell/COM、原生 DLL、IPC、日志、打包。扫描清单见 `security-source-inventory-2026-10-03.csv`。这不是对约 3.8 万行代码逐行完成形式化验证，也不是渗透测试；普通界面与布局计算以全量静态检索、构建及现有测试覆盖。没有将“未匹配危险 API”表述为“绝对安全”。

密钥检查覆盖工作树、忽略的 bin/obj/publish 与本地备份、ZIP 内条目、当前本地 Git 可达历史。共扫描 6,351 个文件、1,924 个 ZIP 条目和 452 个历史 blob，读取约 1.40 GB 数据；模式未命中凭据。排除 `.vs` 缓存；安装 EXE 仅检查原始 ASCII/UTF-16 字符串，未解包其压缩载荷；未抓取远端历史。具体计数和限制见 `security-scan-2026-10-03.json`。

## 2. 依赖核验

仓库原本没有 packages.lock.json；实际版本从 NuGet 还原结果和 obj/project.assets.json 取得。测试工程没有额外第三方测试框架，继承主项目依赖。

| 包 | 类型 | 审核前实际版本 | 修复后实际版本 | 结论 |
|---|---|---|---|---|
| WPF-UI | 直接 | 4.2.0 | 4.3.0 | 旧版被官方弃用，原因 CriticalBugs；是维护/功能缺陷标记，不能据此杜撰 Critical 安全漏洞 |
| System.Management | 直接 | 9.0.0 | 9.0.20 | 同一大版本内更新维护补丁；未发现匹配该包的已知漏洞条目 |
| WPF-UI.Abstractions | 间接 | 4.2.0 | 4.3.0 | 跟随 WPF-UI 实际解析版本升级；未发现匹配条目 |

对以上三个包分别进行了 Web 搜索及 OSV NuGet 版本查询，并用 `dotnet list MagiDesk.slnx package --vulnerable --include-transitive --format json` 交叉核对。修复前后均未返回漏洞条目，修复后 `--deprecated` 也没有返回弃用包。不能由此证明不存在尚未公开或未被数据库收录的问题。本次没有确认需要评估可达性的 Critical/High 依赖漏洞，没有编造 CVE。

来源与证据：

- [WPF-UI 官方 NuGet 版本及弃用记录](https://www.nuget.org/packages/wpf-ui)：4.2.0 已弃用；[官方安全策略](https://github.com/lepoco/wpfui/blob/main/SECURITY.md)只支持最新版本。
- [WPF-UI 官方版本说明](https://github.com/lepoco/wpfui/releases)：4.3.0 为同大版本升级。仍需人工验证主题、导航及弹框外观。
- [System.Management 9.0.20 官方包](https://www.nuget.org/packages/System.Management/9.0.20)与[.NET 9.0.20 官方说明](https://github.com/dotnet/core/blob/main/release-notes/9.0/9.0.20/9.0.20.md)。未擅自跨到 10.x。
- [WPF-UI.Abstractions 官方包](https://www.nuget.org/packages/WPF-UI.Abstractions/)，来源与 WPF-UI 一致。
- OSV 查询端点 `https://api.osv.dev/v1/query`；实际结果保存于 `security-osv-audit-2026-10-03.json`。NuGet 扫描与版本清单分别保存于 `security-nuget-audit-2026-10-03.json`、`security-dependencies-2026-10-03.json`。

本机 SDK 为 10.0.401，.NET / WindowsDesktop 运行时为 10.0.12，与[官方 10.0.12 发布记录](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.12/10.0.12.md)核对。框架引用不等于额外的 NuGet 包；自包含发行包仍需单独核对打包时使用的运行时。本机安装工具为 Inno Setup 6.7.3，[官方变更记录](https://jrsoftware.org/files/is6-whatsnew.htm)含安全相关修正；本项目没有使用其旧 PowerShell 示例。没有发现疑似仿冒包；WPF-UI 4.2.0 是已弃用版本，不是整个项目停止维护。

## 3. 问题汇总

级别为本项目场景下的风险评级，不是 CVSS。位置为修复后源码位置；“加固”不代表已经构造成功的攻击。

| 编号 | 类别 | 严重级别 | 位置 | 状态 |
|---|---|---|---|---|
| SEC-01 | 代码 | Low | MagiDesk/Pages/BrowserBadgesPage.xaml.cs:769 | 已修复：头像目录前缀误判 |
| SEC-02 | 代码 | Medium | MagiDesk/Features/Updates/UpdateService.cs:33、150 | 已修复：跟随跳转后才检查地址 |
| SEC-03 | 代码 | Low | MagiDesk/Features/Updates/UpdateInstaller.cs:28、41 | 已修复：校验与启动之间普通文件可被替换 |
| SEC-04 | 代码 | Medium | MagiDesk/Native/NativeLibraryPolicy.cs:5 | 已修复（加固）：系统 DLL 搜索范围过宽 |
| SEC-05 | 依赖 | Low | MagiDesk/MagiDesk.csproj:45、47 | 已修复（维护）：弃用 UI 版本及旧维护补丁 |
| SEC-06 | 密钥 | Low | .gitignore:62 | 已修复（预防）：未忽略常见环境凭据/签名私钥文件 |
| SEC-07 | 代码 | High | MagiDesk/Features/Updates/SignedUpdateManifest.cs；UpdateInstaller.cs；tools/UpdateSigning | 已修复验证实现；正式密钥与上线验收待完成 |
| SEC-08 | 依赖 | Low | Directory.Build.props、NuGet.Config、scripts/Test-Security.ps1、.github/workflows/security.yml | 已修复：配置和本地验证完成，远端启用待验收 |

### SEC-01：头像文件删除边界

旧代码只检查 `full.StartsWith(AvatarDir)`。例如受管理目录叫 avatars，相邻 avatars-backup 的文件也满足条件。利用需要配置中存在此路径，且用户进行触发头像清理的操作；不是任意远程删除入口。

改为比较规范化后的父目录，只有头像目录直接生成的文件可以进入删除逻辑。新增 `Infrastructure/ManagedFilePath.cs`，测试覆盖正常文件、同名前缀兄弟目录、`..` 和子目录。没有改变用户选择外部图片的能力。

### SEC-02：更新下载跳转校验过晚

原 HttpClient 自动跟随重定向，之后才检查最终主机名。即使最终拒绝使用内容，不允许的目标也可能已经被请求。利用依赖上游返回异常/恶意重定向；更新源固定，不存在普通用户输入任意 URL 的直接 SSRF 接口。

关闭生产 HttpClient 的自动重定向，新增统一的 `GetTrustedAsync`。每次请求前验证 HTTPS、默认端口、无用户名密码、允许的主机；元数据只允许 api.github.com，下载只允许 GitHub 及指定资产 CDN。最多跟随 5 次跳转，异常响应及时释放。测试覆盖 HTTP 降级、localhost、伪造域名、异常端口、userinfo、合法 CDN 和跳转循环，并验证拒绝目标没有收到请求。内部测试注入的 HttpClient 使用假 handler，不是面向外部的可配置入口。

### SEC-03：安装前校验的时间窗口

原助手对文件计算 SHA-256 后关闭句柄，再启动路径；能修改当前用户缓存的本地进程可在此间隙替换普通文件。默认同用户权限下攻击者本来已有代码执行能力，因此按 Low 评级，不宣称通用提权漏洞。

现在通过 `OpenVerified` 返回保留的只读句柄，禁止写入/删除共享，直到启动进程完成才释放；直接创建 EXE 进程，避开 Shell 关联。当前安装器为 `PrivilegesRequired=lowest`。测试验证校验时写入、删除被拒绝，错误摘要拒绝并释放句柄。

此措施仅关闭普通文件替换间隙，**不是发布者身份验证，也不是对已被同用户恶意进程控制的目录、重解析点或管理员运行环境提供安全隔离**。不要把它作为可安全执行任意 AppData 文件的依据。

### SEC-04：系统 DLL 查找加固

所有静态 P/Invoke 导入均为 Windows 系统库（user32、kernel32、shell32、dwmapi、gdi32、ole32、shcore、ntdll）。没有约束时，部分解析可能涉及应用/当前目录。利用需能在被搜索目录放置恶意 DLL，并命中未受 KnownDLL 等机制保护的导入；未逐 DLL 构造劫持实证。

新增程序集级 `DefaultDllImportSearchPaths(System32)`。未修改实验探针的显式绝对路径加载；第三方程序集与 Shell 扩展也不受本程序集特性覆盖。[Microsoft 官方说明](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.defaultdllimportsearchpathsattribute)解释了搜索目录劫持防护。

### SEC-05 / SEC-06：依赖与凭据预防

升级 WPF-UI 4.2.0 → 4.3.0（间接 Abstractions 同步），System.Management 9.0.0 → 9.0.20；未跨大版本。新增 `.env`、`.env.*`、`*.pfx`、`*.p12`、`*.key` 忽略规则，保留 `.env.example` 可提交。已验证 ignore 行为。

没有发现真实硬编码密钥，因此没有虚构环境变量、没有创建空洞的 .env.example、没有轮换任何凭据，也没有改动用户实际配置。若后续发现曾提交的真实密钥，必须到对应平台撤销并生成新密钥，仅删除源码或增加忽略规则不够。

## 4. 其他类别核查

| 类别 | 审查结果与边界 |
|---|---|
| SQL/NoSQL/WMI 注入 | 没有数据库访问；WMI 条件插值是 int/uint PID，未发现字符串拼接进查询的输入入口 |
| 命令执行 | 浏览器启动、实例参数使用 ArgumentList，未通过 cmd/PowerShell 拼接用户输入；用户主动启动 EXE/LNK/文件是产品功能，不当作注入漏洞删除 |
| 路径与文件操作 | 重命名有文件名/设备名校验；新建文本采用 CreateNew；复制移动删除走 Shell 提示；FolderDrop 校验自移动/父子路径。头像清理边界已修复。链接、网络共享及 Shell 扩展仍属于本地系统信任边界 |
| 反序列化 | 配置用 System.Text.Json，布局多态只有 leaf/split 显式白名单；没有 BinaryFormatter、动态类型名实例化、任意 XAML 加载。仍需防范巨大本地配置/图片造成资源耗尽，未发现远程上传入口 |
| 解压 | 运行时没有 ZIP 解压入口；发布脚本只压缩本次生成的临时 payload，未发现 Zip Slip 路径 |
| TLS/加密 | 未发现关闭证书验证、弱密码加密、硬编码 IV/盐；更新使用 SHA-256。完整性与身份认证的区别见 SEC-07 |
| 日志 | 详细浏览器信息已使用 WriteSensitive 并默认关闭；日志限队列和大小。普通异常仍可能包含本地路径，详细模式可能含账号名，分享日志前需脱敏；不是凭据自动上传机制 |
| IPC | 单实例命名 Mutex/Event 仅唤起主窗口，没有任意命令载荷；桌面恢复使用带 GUID 的会话事件。未发现网络监听、命名管道命令服务或 WebView。对象抢占/同用户干扰属于剩余本地风险，未验证跨用户 ACL 攻击 |
| 原生代码 | COM 数组已使用 LPArray；缩略图有尺寸与 checked 算术检查；读取进程命令行检查返回缓冲边界。可选 C++ 探针使用共享节并在 Explorer 内回调，仍需专项动态审计，正式脚本不构建/复制此 DLL |
| Web/扩展 | 没有 Web 服务、HTML 渲染或浏览器扩展，XSS、CSRF、CORS、host_permissions 在当前架构不适用；浏览器微标不是扩展 |
| CI/CD/安装 | 未发现 workflow、pull_request_target 或第三方 Actions。发布脚本校验版本并使用参数调用；安装器默认每用户、最低权限、卸载保留配置。构建工具路径本身仍需来自可信开发环境 |

## 5. 开发者决定与剩余风险

**SEC-07 已取得决定：先保留现有更新流程，签名列为发布前待办。** 这意味着风险仍然存在，不标记为已修复。

- 方案 A：独立私钥签名更新清单，客户端固定公钥；应覆盖版本、架构、文件大小和摘要，并设计轮换与吊销。适合保护 Release 账号单独失陷的情况，但私钥不可和发布令牌放在同一信任边界。
- 方案 B：Authenticode 签名 EXE，客户端验证签名链和预期发布者；需要证书、时间戳、续期/轮换方案。仅检查“任意有效签名”不够。
- 建议：先实施 A 作为更新信任控制，再根据公开发布需要增加 B。代码签名不能保证软件无漏洞，两者也不能抵御签名私钥和发布环境同时被攻破。

其他剩余项：

1. SEC-08 后续修复已完成：两个项目 packages.lock.json 包含普通构建与 x64/ARM64 依赖图，默认锁定还原，NuGet 包源与审计源限官方源，漏洞/审计失败阻止构建。发布脚本复用 Gitleaks 历史和工作树扫描，CI 使用固定提交哈希 Actions 和只读权限，配置 Dependabot；本地及双架构发布验证通过。线上运行及分支保护待推送后验收，见 DEPENDENCY-SECURITY.md。
2. 旧 `publish` ZIP/EXE 仍是旧代码，未删除或覆盖，必须重新构建才能包含修复。本轮未发布任何在线 Release。
3. Shell 缩略图、右键菜单会调用系统/第三方处理器，不能由本项目静态审查证明所有文件解析器安全；应保持 Windows 和 Shell 扩展更新，后续考虑后台进程隔离。
4. 同用户可写配置/安装目录不是管理员安全边界；默认普通权限运行。未执行攻击性重解析点、低完整性进程、跨用户会话实验，这些结论标记为待确认。
5. 没有对未知密钥格式、加密历史文件、EXE 压缩载荷及远端不可达历史作“无泄露”保证。当前扫描无命中不等于可以直接公开所有本地备份。

## 6. 验证

- Release 构建成功，输出至 `%TEMP%\magidesk-security-audit`，未覆盖运行中的应用。
- **339/339 无界面测试通过**，包括本轮新增的目录逃逸、跳转拒绝、合法 CDN/跳转上限、校验文件锁定测试。
- 修复后 NuGet 漏洞查询、弃用查询、OSV 三包查询均无条目。
- 已有 CS9191、WFO0003 两类警告仍存在（WPF 临时项目重复报告共 4 条）；没有新增构建错误。没有为消除 WinForms 警告改坏 WPF Per-Monitor-V2 配置。
- 未启动主应用、真实安装器、原生探针；UI 外观、真正的在线升级和 DLL 加载实际桌面交互仍需用户实机验收。构建与单元测试通过不等于这些项目已验收。

关联待办已写入根目录 TODO.md 的 SEC 部分与 docs/RELEASE.md。

## SEC-08 后续修复说明

原报告第 1、2、4 节描述的是首次盘点状态；本轮已新增锁文件、来源配置和 CI，取代其中“没有锁文件/CI”的现状描述。Gitleaks 更完整规则最初命中历史 XAML 两处快捷键标签，人工核对后加入仅匹配具体文件和完整标签行的例外，历史与工作树扫描通过，没有确认真实凭据泄露。故意改变依赖的临时项目触发 NU1004；未提交文件的虚构令牌被正确检出。两个架构均完成自包含发布到 TEMP，未运行应用、未上传包。

## SEC-07 后续修复说明

用户后续授权实施方案 A，取代此前暂缓决定。客户端与安装助手已独立验证 ECDSA P-256 / SHA-256 签名，公钥嵌入程序集，助手不再信任命令行摘要；无签名无回退。离线工具生成加密 PKCS#8 私钥、签署原始清单并自行验证，发布脚本检查公钥。没有生成或存储生产私钥，空信任配置会安全拒绝更新/打包。正式初始化、离线备份和真实安装升级仍是上线前未完成事项，详见 UPDATE-SIGNING.md。初次审核关于未签名架构的描述保留为历史证据，不能用于描述修复后的更新实现。
