# Workflow Studio Workbench Command 设计

> 当前实现：Workbench Command G7，Workflow Studio `1.2.0`，Core/UI SDK `3.3.0`。

## 1. 三条命令

| 用户语义 | CommandId | 菜单 / 快捷键 | 空闲 | 运行中 |
| --- | --- | --- | --- | --- |
| 验证当前工作流 | `myavalonia.plugin.workflow-studio.command.validate` | Tools / workflow / `F6` | Enabled | Disabled |
| 运行当前工作流 | `myavalonia.plugin.workflow-studio.command.run` | Tools / workflow / `F5` | 最近验证成功时 Enabled | Disabled |
| 取消当前工作流 | `myavalonia.plugin.workflow-studio.command.cancel` | Tools / workflow / `Shift+F5` | Disabled | Enabled |

活动目标不是 Studio Document 时，三项菜单采用 `Hide`，快捷键保留 Host-owned 投影但不可执行。Document 开始
关闭或已经释放后，三条命令全部 fail closed。

## 2. 唯一执行链

```text
MenuItem / KeyBinding（Host 创建并释放）
        │ 同一个 CommandId
        ▼
Host Catalog → Context → Executor（执行前重查）
        │ 当前活动 Document Scope
        ▼
MainDocument : IWorkbenchDocumentCommandTarget
        ├─ Validate → 既有 ValidateDefinition
        ├─ Run      → 既有 WorkflowRunSession → caller-bound Gateway → Action
        └─ Cancel   → 既有 WorkflowRunSession.Cancel
```

工作台命令只是现有用例的薄适配，不复制验证器、Runner、Workflow Action Schema、授权、超时或调用 Scope。
编辑器内原按钮继续工作，也调用同一批私有用例；因此回滚工作台接入不要求删除局部 UI。

## 3. 状态与并发

`CanExecute`、`IsRunning`、运行门闩、Secret 和 RunSession 都属于单个 `MainDocument`。Run 返回真正可等待的
`ValueTask`，不会用 `async void` 提前宣称完成。Target 既接受 Host 传入的取消令牌，也继续使用 Document
ClosingToken；快速重复执行由实例级 `Interlocked` 门闩拒绝。

状态变化按受影响 CommandId 逐条通知。Host 可由同一事实刷新菜单和快捷键，不需要订阅 Studio 的私有属性；
两个同类型 Document 因为各自拥有 Target 和 Scope，可以同时形成不同状态。

## 4. SOLID 与朴素模式

| 原则 | 落地方式 |
| --- | --- |
| SRP | `PluginIds` 管身份，Module 管声明，Document Target 管实例行为，Host 管投影与路由 |
| OCP | 新命令可追加 Descriptor/Placement 和 Target 分支，不修改 Host 执行器或 Workflow Runner |
| LSP | Host 只调用 SDK Target 接口；真实 Studio、模板 Target 和测试替身遵守同一契约 |
| ISP | Target 只暴露状态查询、异步执行和定向事件，不取得 Provider、Dock、Control 或 Registry |
| DIP | Studio 精确依赖公开 NuGet；Host 只消费真实 ZIP 和 SDK 抽象，双方没有源码引用 |

使用的模式只有稳定身份常量、不可变描述符、窄适配接口和实例状态通知。没有引入 Mediator、事件总线、反射
命令发现、字符串 `when` 表达式、服务定位器或第二套执行管线。

## 5. 测试与门禁

Studio 独立入口：

```powershell
pwsh -NoProfile -File .\scripts\Test-WorkflowStudioG7.ps1 -Configuration Release
```

它验证精确 NuGet/lock file、零警告构建、格式、54 项单测、85%/75% 总覆盖率、MainDocument 至少 90%、
Standalone Fake 闭环、两轮确定性 ZIP、manifest、共享 SDK 排除、Secret canary 和文档链接。Host 侧另通过
真实 ZIP、生产 Loader、独立 ALC、两个 Document Scope、真实菜单/快捷键以及跨 ALC Action 复核整条链。

G7 是本地开发门禁：`aiflow=false`，不调用 Windows CI/Smoke、Release Acceptance 或发布门禁，也不上传、
签名或创建 tag。

完整实数与回滚边界见 [G7 专用实施记录](plan-history/workbench-command/g7-workflow-studio-three-real-commands.md)。
