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
Host 授权、ALC 或 Provider Scope。真实加载仍由 G3 门禁把正式 ZIP 部署到候选 Host 隔离副本后启动验证。

## 安全规则

- 同一 Workflow Studio 插件不注册 Provider，避免违反 SDK 的 Provider/Consumer 互斥边界；
- Fake 类型只存在于 `WorkflowStudio.Standalone`，正式 ZIP 只有 Plugin DLL、deps、PDB 和 manifest；
- 运行记录只保存步骤、ForEach 索引、InvocationId、终态和 Host 脱敏失败，不保存参数或输出；
- Action 的敏感指针只能由会话 Secret 引用满足。
