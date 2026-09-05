# Workflow Action Consumer 与 Fake/真实 Gateway 边界

Workflow Studio 是纯 Consumer。`WorkflowStudioModule.Configure` 调用 `UseWorkflowActionGateway()`，真实 Host
据 manifest 的 `myavalonia.plugin.workflow-studio` 身份注入 caller-bound Gateway。请求中只有 ActionId 和
JSON arguments，Studio 不能提交 CallerId、RunId、InvocationId、OwnerId 或授权结果。

## 真实 Host

真实 Host 负责不可变动作目录、Owner 可用性、Schema 最终校验、授权、调用/所有者并发、超时、Provider
invocation scope、进度限流脱敏和关闭排空。Studio 只做编辑器需要的目录投影与预检，不解析其他插件
Provider，也不引用 Handler 程序集。

每次点击执行创建一个 `IWorkflowActionRun`，使 OncePerRun 授权与取消状态只属于该次工作流。无论成功、
失败、验证异常还是取消，Runner 都通过 `await using` 释放 Run。

## Standalone Fake

Standalone 不注册 Provider 插件，而是在自身组合根中注入一个实现公开 SDK 端口的 Fake Gateway：

| Fake Action | 职责 | 风险与确认 |
| --- | --- | --- |
| `generate-items` | 生成最多 10 个测试项，提供前序数组输出 | `None` / `Never` |
| `format-item` | 顺序消费 item 与会话 Secret，不回显 Secret | `HandlesSecret` / `OncePerRun` |

该 Fake 能证明 UI、目录、定义、引用、ForEach、Secret 展开、进度、失败停止、取消和 Run 释放，但不会模拟
Host 授权、ALC 或 Provider Scope。真实加载仍由 G3.1 门禁把正式 ZIP 部署到候选 Host 隔离副本后启动验证。

G3.1 的 Schema Profile、实例验证、保守可赋值、引用路径和双 revision 算法来自共享 Workflow SDK。
正式插件只引用该契约，ZIP 不携带 `MyAvaloniaManagement.PluginSdk.Workflow.dll`；候选 Host 必须通过
当前 manifest 的 SDK 下限 `3.3.0` 保证默认 ALC 已提供 Workbench Command 契约；Workflow SDK 仍为 `1.0.0`。

## 安全规则

- Workflow Studio 保持纯 Consumer；这是产品职责分工，Host 已允许其他插件在治理约束下使用双角色；
- Fake 类型只存在于 `WorkflowStudio.Standalone`，正式 ZIP 只有 Plugin DLL、deps、PDB 和 manifest；
- 运行记录只保存步骤、ForEach 索引、InvocationId、终态和 Host 脱敏失败，不保存参数或输出；
- Action 的敏感指针只能由会话 Secret 引用满足。

## G0013 内置示例的恢复边界

内置 Fractal → ImageLab 示例在 Scoped 台账保留当前冻结定义的必要 Artifact 字段与逐项状态，
不把任意 Action 输出加入通用运行记录，不保存 Secret，也不持久化。关闭立即清空台账。
显式清理在应用层逐项创建 Release Run；一项失败不阻断其余项，确认和取消仍由 Host Gateway 负责。
示例生成与续跑细节见 [G0013 专用文档](refactoring/G0013/README.md)。
