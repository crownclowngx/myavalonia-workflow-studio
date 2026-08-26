# Workflow Definition v1 与执行语义

> 历史文档：本格式只属于 G3。Workflow Studio 1.1.0 的 Codec 已硬切 v2，明确拒绝 v1；
> 不提供隐式迁移、兼容导入或混合版本解释。当前格式见 [Workflow Definition v2](workflow-definition-v2.md)。

## 定义边界

定义格式是 Workflow Studio 私有数据，不属于 Plugin SDK，也不进入 Host Document 信封。G3 的 Document
是非持久化对象，关闭后定义与运行记录直接丢弃。规范字段固定如下：

```json
{
  "schemaVersion": 1,
  "catalogRevision": "sha256:...",
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

只允许以下完整值引用：

- `${step-id.result.path}`：已经完成的前序步骤输出；
- `${item.path}`：当前 ForEach 项，只能出现在该 ForEach 步骤内；
- `${secret.name}`：当前 Document 会话 Secret。

引用不能嵌入普通字符串，也不支持运算符、函数、条件或脚本。ForEach 来源只能是前序输出 Schema 中带
`items` 的有界数组，执行顺序与数组顺序一致。

## 确定性验证

目录 revision 对按 ActionId 排序后的公开 Descriptor、输入/输出 Schema、风险、确认策略和敏感指针做
规范化 SHA-256。点击执行时 Runner 会重新读取目录并再次验证；Action 新增、删除、改名、Schema 或风险
变化都会使旧定义失效。

默认预算：定义 256 KiB、32 个步骤、单次 ForEach 100 项、总展开调用 256 次、字符串 64 KiB、总运行
6 小时。Host 仍会在调用边界执行最终 Schema、授权、超时、输出与 Provider Scope 治理。

## Runner 与失败语义

每次工作流只创建一个 `IWorkflowActionRun`。普通步骤和 ForEach 项严格串行；首个 Failed、Rejected、
Unavailable、TimedOut 或 Cancelled 终态会停止剩余调用。Runner 在所有返回和异常路径上异步释放 Run，
并在结束时清除仅供引用解析的输出快照。

G3 不自动重试。SDK 3.1 没有公开“可安全重试”元数据，由 Studio 猜测动作幂等性会破坏所有权和副作用
语义，因此重试留待未来有明确契约的阶段。

## Secret

敏感 JSON Pointer 必须绑定 `${secret.*}`，验证器拒绝明文常量。Secret Store 属于 Document Scope，替换、
关闭和释放时覆盖内部字符数组；值只在调用前展开，定义、规范导出、运行摘要、诊断、TRX 和门禁摘要
均不保存输入、输出或 Secret 值。
