# MyAvalonia Workflow Studio

这是使用 `MyAvaloniaManagement.Plugin.Templates 1.1.0` 创建、随后独立演进的 Managed Plugin 解决方案，
稳定 PluginId 为 `myavalonia.plugin.workflow-studio`。当前插件版本为 `1.2.0`，精确消费 Core/UI SDK
`3.3.0`、Workflow SDK `1.0.0` 与 Build `1.1.2`。Workflow Action G3.1 已实现不依赖模型、API Key 或
规划网络的手工工作流 MVP；Workbench Command G7 又把验证、运行和取消接入 Host 统一命令系统，原编辑器
按钮与 Runner/Workflow Action 治理链保持不变。

真实交付物只有 `src/WorkflowStudio.Plugin`。Standalone 复用同一份 Document、View 和业务服务，但只为
公开 `IWorkflowActionGateway` 与 `IDocumentLifetime` 提供开发期 Fake；正式 ZIP 不包含 Fake、Standalone
或 Tests。

## 快速验证

```powershell
dotnet restore .\WorkflowStudio.slnx --locked-mode
dotnet build .\WorkflowStudio.slnx -c Release --no-restore -warnaserror
dotnet test .\WorkflowStudio.slnx -c Release --no-build --no-restore
dotnet run --project .\src\WorkflowStudio.Standalone -c Release
```

无窗口执行 Fake 闭环：

```powershell
dotnet run --project .\src\WorkflowStudio.Standalone -c Release -- --g3-self-test
```

Workbench Command G7 独立非发布门禁：

```powershell
pwsh -NoProfile -File .\scripts\Test-WorkflowStudioG7.ps1 -Configuration Release
```

该入口只从 NuGet.org locked restore，执行零警告构建、54 项测试、覆盖率、Standalone Fake 闭环、两轮
确定性 ZIP、manifest/共享 SDK/Secret/文档断言；不调用 AIFLOW、Windows CI/Smoke 或发布门禁。

完整 G3.1 本地非发布门禁需要一个已构建的候选 Host 输出目录和隔离候选 feed：

```powershell
pwsh -NoProfile -File .\scripts\Test-WorkflowStudioG3.1.ps1 `
  -Configuration Release `
  -CandidateFeed C:\Path\To\CandidateFeed `
  -CandidateHostRoot C:\Path\To\CandidateHost\bin\Release\net10.0
```

该入口执行 locked restore、零警告构建、测试与 85%/75% 覆盖率、Standalone 自检、两次确定性 ZIP、
Secret 扫描和隔离真实 Host 启动。它不调用 AIFLOW、Windows CI、Release Acceptance、发布门禁、标签、
签名或上传。

## 文档

- [项目文档入口](docs/README.md)
- [定义 v2 与执行语义](docs/workflow-definition-v2.md)
- [Workflow Action 与 Fake/真实 Gateway 边界](docs/workflow-actions.md)
- [Workbench Command 三命令设计](docs/workbench-commands.md)
- [Workbench Command G7 专用实施记录](docs/plan-history/workbench-command/g7-workflow-studio-three-real-commands.md)
- [G3.1 专用实施记录](docs/plan-history/workflow-action/g3.1-protocol-consistency-and-reference-safety.md)
- [部署与非发布验收](docs/deployment-and-release.md)
