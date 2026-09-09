# G0014 归档 Workflow 业务结果适配

2026-09-09。本轮配合 `myavalonia-layer-unpack` R08 无密码归档动作实施，仅修改 Runner 的业务结果识别，不增加编辑器、调度、密码参数、后台服务或定义字段。沿用 SOLID、朴素实现和中文注释；不使用 AIFLOW、Windows CI 或发布门禁。

## 问题与实现

归档动作的合法 JSON 可以表达部分失败。此前 SDK Succeeded 会使 Studio 最终显示“工作流执行成功”，无法区分业务失败。新增 `ArchiveWorkflowOutcome` 仅识别公开两个 v1 Action ID 及 `myavalonia.layer-unpack.workflow-result` 契约，使用 BCL JSON，不引用归档插件类型或容器。

completed/skipped 保持原行为；partial-failure/failed 的成功输出继续供后续步骤引用，并允许正常释放节点执行，最终 Succeeded=false，条目附白名单 `archive.partial-failure`／`archive.failed`。SDK Status 本身不伪造。非法契约或未知版本记 `archive.result-invalid` 并停止后续消费。其他 Provider 不受影响，错误正文不直接进入摘要。

## 测试与门禁

`ArchiveWorkflowOutcomeTests` 覆盖状态、版本、脱敏、其他动作隔离和“成功引用继续／最后如实失败”；归档仓库的独立集成项目通过三个真实 ALC 和真实 Studio 验证样例。运行 `dotnet test tests/WorkflowStudio.Tests/WorkflowStudio.Tests.csproj -c Debug`；联合门禁由归档仓库 `tools/verify-local.ps1` 执行，包括完整 Studio 测试。

全局 SDK 缓存曾有同版本本地包，联合门禁使用归档仓库 `artifacts/nuget-official` 和 NuGet.org，从本项目既有锁文件严格还原；不降低哈希校验。

## 边界

最终本地验证：Studio Debug 构建零警告，完整测试 **86/86**（其中本轮新增 13 项），零失败、零跳过；归档联合验证共 **597/597**。原始 TRX 与逐步检查记录在相邻归档仓库 `artifacts/local-verification/`，可提交摘要在其 `docs/refactoring/G0014/verification-summary.json`。本轮三个改动源文件通过定向 dotnet format 验证，文档链接与 diff 空白检查通过。

定义 v2、结果引用语法和 Secret 会话不变。本改动不赋予通用 finally／补偿节点；SDK 失败或取消仍按原规则停止。真实宿主安装／授权窗口／原生退出和正式发布不在本轮门禁内。
