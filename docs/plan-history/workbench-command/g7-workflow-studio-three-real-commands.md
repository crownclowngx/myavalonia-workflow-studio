# Workbench Command G7：Workflow Studio 三条真实命令

> 状态：已完成（2026-08-28）；本地非发布门禁通过。
>
> Studio 输入提交：`0b3a3f55f43e66a914099f011dd344e7f556b56e`
>
> Host 输入提交：`97732d21ad16676a38a298d6a8fda3140d467759`
>
> 设计说明：[Workflow Studio Workbench Command](../../workbench-commands.md)

## 1. 目标与冻结边界

G7 将 Validate、Run、Cancel 三个真实用户语义接入 Host Workbench Command，同时保持 Workflow Definition v2、
Validator、Secret、Risk、`WorkflowRunSession`、caller-bound Gateway 和 Action Provider 的既有分层。Studio 从
Core/UI `3.2.0` 升到已发布的精确 `3.3.0`，业务版本从 `1.1.0` 升到 `1.2.0`，manifest 最低 SDK 为
`[3.3.0, 4.0.0)`；Workflow SDK `1.0.0` 和 Build `1.1.2` 不变。

外部仓库没有 Host/SDK `ProjectReference`，Host 仓库也没有把 Studio 加入解决方案。两边只在 G7 门禁中通过
公开 NuGet 和真实插件 ZIP 相交。

## 2. 实现与设计思路

```text
WorkflowStudioModule（声明）
  ├─ 3 × CommandDescriptor
  ├─ 3 × ToolsShared Menu Placement：workflow / 0,10,20 / Hide
  └─ 3 × KeyBinding：F6 / F5 / Shift+F5
                    │
                    ▼ Host 当前实例路由
MainDocument（窄 Target）
  ├─ ValidateDefinition
  ├─ WorkflowRunSession.RunAsync → Gateway → Action
  └─ WorkflowRunSession.Cancel / ClosingToken
```

`MainDocument` 显式实现 `IWorkbenchDocumentCommandTarget`，避免与既有 `CanExecute` 布尔属性混淆。模块不注册
回调、`ICommand`、Provider 或 UI 对象。Run 返回真实异步完成，实例级门闩拒绝快速重复运行；关闭开始后统一
fail closed。状态事件只携带具体 CommandId，避免“刷新全部”的隐藏协议。

这是 Adapter + 不可变 Descriptor 的朴素组合。SRP、OCP、LSP、ISP、DIP 的逐项说明见设计文档；未加入
Mediator、事件总线、Service Locator、反射发现或第二套 Runtime。

## 3. 测试证据

Studio 单测为 **54/54**，覆盖状态矩阵、未知 ID、预取消、关闭 fail closed、真实 Run/Cancel、有效/无效
Secret、两个 Document 实例隔离，以及三组注册声明。总行/分支覆盖率为 **89.78% / 83.95%**，
`MainDocument.cs` 行覆盖率为 **91.39%**。

Standalone `--g3-self-test` 完成 4 次 Fake Action 调用并释放 1 个 Run。两轮正式 ZIP 均为 4 个文件，SHA-256
均为 `27C0C59BD7CC08AB7035AF777BB9C1A3D397258B37D363EDF2EC7CD88F2F2E6D`；ZIP 不含 Standalone、Tests
或 Host 共享 SDK，manifest 为 schema 2、插件 `1.2.0`、SDK `[3.3.0, 4.0.0)`。

Host 专项另验证：

- 真实 ZIP 经生产 Loader 进入独立 ALC，Core/UI SDK 仍来自默认 ALC；
- 3 条 Command、3 个 Tools 菜单声明和 3 个快捷键声明完整，两个真实 Document Scope 相互隔离；
- Run 经 caller-bound Gateway 调用第二个独立 ALC 中的真实 echo Action，结果为“工作流执行成功”；
- MainWindow Headless UI 中菜单和快捷键共享同一命令对象，`F6` 只改变当前 Studio Document；
- 非 Studio 目标时菜单隐藏、快捷键禁用，窗口释放后 KeyBinding 归零。

## 4. 门禁入口与结果

```powershell
# Studio 独立公开源、测试、覆盖率和确定性包
pwsh -NoProfile -File .\scripts\Test-WorkflowStudioG7.ps1 -Configuration Release

# 在 Host 仓库中组合基础开发门禁、真实包和 Headless UI
pwsh -NoProfile -File .\scripts\Test-WorkbenchCommandG7.ps1 -Configuration Release
```

Studio 摘要位于 `artifacts/test-results/WorkflowStudioG7/summary.json`。Host 当前基础门禁为 **573/573**，
行/分支覆盖率 **86.98% / 72.39%**；G7 真实包 PluginTests 为 **2/2**，Headless UI 为 **1/1**。

## 5. 非发布与回滚

G7 只生成本地忽略制品，不形成发布资格，不上传 WorkflowStudio `1.2.0`，不签名、不打 tag。未使用 AIFLOW，
未调用 Windows CI、Windows Smoke、Release Acceptance 或发布门禁。

回滚时整体移除 Command/Placement 身份、Module 声明和 `MainDocument` Target，恢复 Studio 业务版本/SDK 下界及
lock file，并移除 G7 门禁/记录。原编辑器按钮、Validator、Runner 和 Workflow Action 功能必须保留，不能通过
删除旧局部 UI 换取单路径表象。

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
