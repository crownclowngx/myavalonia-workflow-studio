# MyAvalonia Workflow Studio

这是使用 `MyAvaloniaManagement.Plugin.Templates 1.1.0` 创建的独立 Managed Plugin 解决方案，稳定
PluginId 为 `myavalonia.plugin.workflow-studio`。G3 已实现不依赖模型、API Key 或规划网络的手工工作流
MVP：结构化编辑、定义 v1、确定性验证、会话 Secret、风险摘要、顺序/有限 ForEach Runner，以及
Standalone 两个 Fake Action 闭环。

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

完整 G3 本地非发布门禁需要一个已构建的候选 Host 输出目录：

```powershell
pwsh -NoProfile -File .\scripts\Test-WorkflowStudioG3.ps1 `
  -Configuration Release `
  -CandidateHostRoot C:\Path\To\CandidateHost\bin\Release\net10.0
```

该入口执行 locked restore、零警告构建、测试与 85%/75% 覆盖率、Standalone 自检、两次确定性 ZIP、
Secret 扫描和隔离真实 Host 启动。它不调用 AIFLOW、Windows CI、Release Acceptance、发布门禁、标签、
签名或上传。

## 文档

- [项目文档入口](docs/README.md)
- [定义 v1 与执行语义](docs/workflow-definition-v1.md)
- [Workflow Action 与 Fake/真实 Gateway 边界](docs/workflow-actions.md)
- [G3 专用实施记录](docs/plan-history/workflow-action/g3-workflow-studio-fake-action-loop.md)
- [部署与非发布验收](docs/deployment-and-release.md)
