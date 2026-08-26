# Workflow Definition v2 与执行语义

## 严格根协议

Workflow Studio 1.1.0 只接受以下根字段，字段缺失、未知、重复、`schemaVersion: 1` 或混合版本均返回
脱敏格式错误，不进入迁移分支：

```json
{
  "schemaVersion": 2,
  "contractRevision": "sha256:...",
  "presentationRevision": "sha256:...",
  "summary": "处理临时测试项",
  "steps": [
    {
      "id": "generate",
      "actionId": "myavalonia.plugin.workflow-studio-fake.workflow.generate-items",
      "arguments": { "count": 3, "prefix": "条目" }
    },
    {
      "id": "format",
      "forEach": "${generate.result.items}",
      "actionId": "myavalonia.plugin.workflow-studio-fake.workflow.format-item",
      "arguments": {
        "value": "${item.value}",
        "secret": "${secret.session-key}"
      }
    }
  ]
}
```

Contract revision 包含 Action ID、风险、确认策略、敏感指针及移除 `description` 后规范化的输入输出
Schema。对象属性、`required`、`enum` 等语义无序集合不受声明顺序影响。Presentation revision 只跟踪
Action 名称、说明和 Schema description。契约漂移产生 Error 并禁止执行；仅展示漂移产生
`catalog.presentation-stale` Warning，定义仍可执行。

## 静态引用安全

- `${step-id.result.path}` 只能引用前序步骤中由 `required` 保证存在的输出路径；
- `${item.path}` 只允许出现在 ForEach 内，并同样要求每个对象段为 required；
- 数组段必须是非负十进制整数，且静态索引必须由来源 `minItems` 保证存在；
- 来源 Schema 的全部合法值必须能赋给目标 Argument Schema；只允许 `integer → number` 的基础类型放宽；
- 当前会话 Secret 用真实字符串值校验目标 Schema，不假设无限宽字符串；
- ForEach 来源必须是保证存在的有界数组，其步骤输出静态形状为“Action 输出数组”。

Schema、实例、路径和可赋值语义来自 `MyAvaloniaManagement.PluginSdk.Workflow 1.0.0`，Host 与 Studio
不再维护两套 Rune、decimal 或数组路径算法。

## 运行失败与数据寿命

运行时引用失败由 `WorkflowReferenceResolutionException` 表达，再转换为包含 Code、StepId、ItemIndex、
Path 和脱敏 Message 的 `WorkflowRunFailure`。失败发生在调用前时不伪造 invocation entry，也不会冒泡到
UI Command。Runner 仍只创建一个 caller-bound Run，串行执行步骤和有限 ForEach，并在所有返回路径释放。

定义预算为 256 KiB、32 步、单个 ForEach 100 项、总调用估算 256 次、总运行 6 小时。Secret 与中间输出
只属于当前 Document 运行会话；关闭、取消和释放会清理它们，定义、日志、TRX、ZIP 与摘要不保存其正文。
