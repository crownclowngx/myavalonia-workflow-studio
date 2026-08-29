# Workbench Command G10：WorkflowStudio 本地封板

> 状态：已完成（2026-08-29；跨仓单轮完整本地非发布门禁）。
>
> 输入提交：`a817ab226dd5b0ceee65eb5be8cbafc468cea2f6`
>
> 前置：[G7 三条真实命令](./g7-workflow-studio-three-real-commands.md)

## 1. 结论与设计

G10 没有修改 WorkflowStudio 生产代码、public API、Workflow Definition v2、Runner 或 Workflow Action
Gateway。插件仍为 1.2.0，精确消费 Core/UI 3.3.0 和 Workflow SDK 1.0.0，manifest schema 2 与 SDK
区间 `[3.3.0,4.0.0)` 不变。

SOLID 仍是首要纪律：G7 叶子脚本拥有还原、构建、测试、覆盖率、Standalone、ZIP 和 manifest 规则；
`Test-WorkflowStudioG10.ps1` 只验证 G7 摘要并投影稳定事实。MainDocument 继续通过窄
`IWorkbenchDocumentCommandTarget` 适配 Validate/Run/Cancel，Run 继续进入 caller-bound Gateway，
没有第二套 Runner、事件总线或服务定位器。

## 2. 本仓与 Host 验收

```powershell
pwsh -NoProfile -File .\scripts\Test-WorkflowStudioG10.ps1 -Configuration Release
```

复用的 G7 实测为 **54/54**，失败 0、跳过 0；总行/分支覆盖率 **89.78% / 83.95%**，MainDocument
行覆盖率 **91.39%**。两次确定性 ZIP 均为 4 个文件；最终跨物理根 SHA-256 由 Host G10 的
`artifacts/test-results/WorkbenchCommandG10/summary.json` 记录，本文不预填依赖旧构建根的哈希，
避免门禁完成后回写本文而使已签署的工作树指纹失效。

Host G10 另把本包与 ClassicGame 实体包同时加载，验证 25 条外部命令共同注册、Studio 菜单/快捷键/Palette
只随当前 Studio Document 显示，并在切换、关闭和窗口退出后解除订阅。完整跨仓证据由 Host 的
`artifacts/test-results/WorkbenchCommandG10/summary.json` 保存。

## 3. 非发布与回滚

本轮不使用 AIFLOW，不运行 Windows CI、Windows Smoke、Release Acceptance 或发布门禁，不上传、不签名、
不打 tag，Release 仅是编译配置。回滚只移除 G10 包装脚本、记录和索引；G7 三条命令及原编辑器按钮保持。

```text
aiflow=false
windowsCi=false
windowsSmoke=false
releaseAcceptance=false
releaseGate=false
publishable=false
published=false
uploaded=false
signed=false
tagCreated=false
```
