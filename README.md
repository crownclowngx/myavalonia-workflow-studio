# MyAvalonia Workflow Studio

> V6.1 图标同步升级：插件 `1.2.1`，Core/UI SDK `3.4.0`，Build `1.1.3`。
> 图标映射、兼容边界与验证命令见 [专用说明](docs/plan-history/v6.1-plugin-icons.md)。

G0013 已增加 Document 内置“Fractal → ImageLab”示例：1–16 个已保存配方、完整 Definition v2、
保留成功 PNG、会话续跑与显式清理。见 [专项入口](docs/refactoring/G0013/README.md)与[设计和使用](docs/refactoring/G0013/implementation.md)。
本阶段使用 Debug 本地门禁，不运行下文历史 Host 封板/ZIP 命令，也不增加 Windows CI 或发布门禁。

这是使用 `MyAvaloniaManagement.Plugin.Templates 1.1.0` 创建、随后独立演进的 Managed Plugin 解决方案，
稳定 PluginId 为 `myavalonia.plugin.workflow-studio`。当前插件版本为 `1.2.1`，精确消费 Core/UI SDK
`3.4.0`、Workflow SDK `1.0.0` 与 Build `1.1.3`。Workflow Action G3.1 已实现不依赖模型、API Key 或
规划网络的手工工作流 MVP；Workbench Command G7 又把验证、运行和取消接入 Host 统一命令系统，原编辑器
按钮与 Runner/Workflow Action 治理链保持不变。G10 已与 Host、ClassicGame 完成单轮完整跨仓本地封板，
不形成发布资格。

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

跨仓 Workflow/Workbench 验证由主仓统一 Gate 执行，不再由本仓维护 PowerShell 封板脚本：

```powershell
dotnet run --project tools/MyAvaloniaManagement.Gate -- verify --scope workflow
dotnet run --project tools/MyAvaloniaManagement.Gate -- verify --scope workbench
```

上述命令在 `avalonia_dock_simple_test` 主仓根目录运行；若本仓不位于默认目录，可显式传入：

```powershell
dotnet run --project tools/MyAvaloniaManagement.Gate -- verify --scope workflow `
  --workflow-studio C:\Path\To\myavalonia-workflow-studio
```

正式 `seal` 会直接运行本仓 locked restore、零警告构建、测试、覆盖率、Standalone Fake 闭环和两次确定性
ZIP，并把当前工作树内容指纹写入主仓统一证据。本仓的历史 G3/G7/G10 命令仅保留在实施记录中。

单独开发本仓仍可直接运行标准 .NET 命令和无窗口 Fake 自检：

```powershell
dotnet restore .\WorkflowStudio.slnx --locked-mode
dotnet build .\WorkflowStudio.slnx -c Release --no-restore -warnaserror
dotnet test .\WorkflowStudio.slnx -c Release --no-build --no-restore
dotnet run --project .\src\WorkflowStudio.Standalone -c Release -- --g3-self-test
```

Gate 不调用外部发布、签名、上传或标签操作。

## 文档

- [项目文档入口](docs/README.md)
- [定义 v2 与执行语义](docs/workflow-definition-v2.md)
- [Workflow Action 与 Fake/真实 Gateway 边界](docs/workflow-actions.md)
- [Workbench Command 三命令设计](docs/workbench-commands.md)
- [Workbench Command G7 专用实施记录](docs/plan-history/workbench-command/g7-workflow-studio-three-real-commands.md)
- [Workbench Command G10 本地封板记录](docs/plan-history/workbench-command/g10-workflow-studio-local-sealing.md)
- [G3.1 专用实施记录](docs/plan-history/workflow-action/g3.1-protocol-consistency-and-reference-safety.md)
- [部署与非发布验收](docs/deployment-and-release.md)
