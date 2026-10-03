# 依赖与发布安全检查（SEC-08）

## 日常构建

根目录 `Directory.Build.props` 默认开启 NuGet 锁定还原。两个项目各自的 `packages.lock.json` 记录直接/间接包实际版本和内容摘要，同时包含普通构建、win-x64、win-arm64 的依赖图。必须随源码一起提交；不要手工编辑摘要。

`NuGet.Config` 清除继承的包源和映射，仅允许官方 nuget.org；漏洞数据也来自官方源。已存在于全局缓存的包不会被来源映射重新下载，锁文件摘要仍参与一致性检查；CI 不启用跨运行的 NuGet 缓存。首次锁文件是在已核查依赖来源后生成的，不是独立的发布者签名。

`global.json` 选择 .NET SDK 10.0.401，允许同 feature band 的稳定补丁，拒绝预览版。NuGet 锁文件不锁整个 Windows/SDK，也不代替对自包含运行时的更新检查。

所有级别的已知依赖漏洞，以及审计数据访问失败（NU1900/NU1905），都作为还原错误处理。检查暂时失败时修复网络后重试，不把失败当作安全通过。普通 `--no-restore` 构建不会重新访问漏洞库；发布前脚本会强制重新还原和审计。

## 主动更新依赖

先修改 csproj 中的版本，再在仓库根目录执行：

```powershell
dotnet restore MagiDesk.slnx --force-evaluate -p:RestoreLockedMode=false
powershell -NoProfile -File scripts/Test-Security.ps1
```

核对两个锁文件中的包名、版本和摘要变化，运行构建及无界面测试，验证两种架构的自包含发布。解锁参数仅用于明确的依赖升级，不放进普通构建、CI 或发布命令。大版本升级仍需单独评估。

## 本地检查与发布

`scripts/Test-Security.ps1` 可独立运行；`Publish-Release.ps1` 已在生成包前调用它。失败会终止发布，不产生本次发行包。

检查内容：

1. 两个锁文件必须存在；强制锁定还原、刷新漏洞数据，检查所有直接和间接包。
2. 下载固定的 Gitleaks 8.30.1 Windows x64 工具，先核对代码中固定的 SHA-256，才能执行。
3. 扫描所有本地可达 Git 分支历史；扫描当前受版本管理及未忽略的新文件，因此尚未提交的修改也会检查。
4. 输出全部脱敏，只展示命中规则、位置和指纹；工具、源码临时副本及临时报告结束后清理。

扫描不包含 Git 忽略的本地产物、未抓取的远程历史、压缩内容或任意未知格式秘密；首次审计的更广范围结果保留在原审计报告中。不要因此把包含个人文件的忽略目录公开发布。脚本针对 Windows x64 构建主机；ARM64 发布是交叉发布，工具并非 ARM64 原生工具。

`.gitleaks.toml` 仅为 WindowDragPage.xaml 两种完整快捷键标签行添加 generic-api-key 误报例外，未禁用通用密钥规则或排除整个文件。新增例外必须逐项核对，真实泄露需先撤销密钥，不能用例外掩盖。

## 持续检查

`.github/workflows/security.yml` 在 push、pull_request、手动触发和每周定时运行：安全检查 → Release 构建和 339 项现有无界面测试 → x64/ARM64 自包含发布验证。不会运行主应用或安装器，不上传发行包。

使用只读 contents 权限、完整提交哈希固定的官方 checkout/setup-dotnet Actions，不持久化 checkout 凭据，不使用 pull_request_target 或发布密钥。Gitleaks 的版本和摘要也固定，升级时需重新核对官方发布资产。

Dependabot 已配置每周检查 NuGet 与 GitHub Actions 更新，不自动合并。Gitleaks CLI 的版本/摘要需人工维护。

**生效边界：** 本轮只修改本地文件。推送后需在 GitHub 确认 Actions/Dependabot 可用，并将 `Security and build / verify` 设置为分支保护必需检查；本轮未修改远程仓库规则。定时任务只有进入默认分支后才执行。

## 本轮验证

- 锁定模式 Release 构建成功，339/339 无界面测试通过。
- win-x64 和 win-arm64 自包含发布均成功，输出仅在 TEMP。
- 独立临时项目故意变更依赖，触发 NU1004，原锁文件未改动。
- Gitleaks 全历史与当前工作树检查通过；在未提交的临时文件加入虚构令牌时成功阻止检查，测试文件已移除。
- GitHub 托管运行和分支保护状态尚未验证，不等同于已上线启用。

参考：[NuGet 锁文件](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies)、[NuGet Audit](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages)、[Gitleaks 官方文档](https://github.com/gitleaks/gitleaks)。

SEC-07 后续新增 `tools/UpdateSigning` 无第三方依赖的离线工具项目，其锁文件也已纳入强制检查，CI 构建整个解决方案。生产公钥初始化与签名流程见 UPDATE-SIGNING.md。
