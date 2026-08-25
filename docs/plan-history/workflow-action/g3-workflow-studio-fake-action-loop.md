# Workflow Action G3：外部 Workflow Studio 与 Fake Action 闭环

> 状态：已完成（2026-08-25）。
>
> 模板：`MyAvaloniaManagement.Plugin.Templates 1.1.0`
>
> SDK：Core/UI `3.1.0`；Build `1.1.2`
>
> 权威摘要时间：`2026-08-25T07:16:22.5240694Z`（北京时间 2026-08-25 15:16:22）

## 1. 结论与边界

G3 在 Host 仓库之外创建 `myavalonia-workflow-studio/WorkflowStudio.slnx`。模板命令直接把解决方案写入
目标子目录，没有额外 `WorkflowStudio` 目录。正式插件只从 NuGet.org 精确还原公开包，不引用 Host 源码、
本地 feed 或 Host artifacts。

插件是纯 Workflow Action Consumer，并贡献一个非持久化 Studio Document。Standalone 用两个窄 Fake
Action 完成“生成列表 → 前序数组引用 → 顺序 ForEach → item 与 Secret 引用 → 失败/取消停止”的闭环。
真实 Host 继续拥有授权、ALC、Provider Scope、预算与关闭排空。

G3 没有使用 AIFLOW，没有接入 Windows CI、Release Acceptance 或发布门禁，也没有创建标签、签名、上传
或发布任何包。Release 仅表示编译配置。

## 2. 设计与 SOLID

| 原则 | G3 做法 |
| --- | --- |
| SRP | Catalog、Codec、Validator、Risk、Secret、Resolver、Runner 与 Document 分责 |
| OCP | 新 Action 通过 Descriptor 目录进入编辑器和 Runner，不修改调用内核 |
| LSP | Standalone/Test Gateway 完整替代 SDK Gateway，不要求 Consumer 特判 |
| ISP | 每个业务能力使用一个小接口，不给 UI 暴露 ServiceProvider |
| DIP | 核心只依赖 SDK Gateway 和 Studio 端口，组合根选择 Host 或 Fake |

实现只使用构造注入、不可变目录快照和朴素 Gateway 适配。没有引入工作流框架、Mediator、事件总线、
Service Locator、脚本引擎或通用管线。

定义 v1、引用语法、预算和失败语义见[定义专项文档](../../workflow-definition-v1.md)。Secret 只存在于
Document Scope，会话关闭时清空；敏感字段必须使用 `${secret.*}`，定义、导出、运行摘要和机器证据不保存值。

## 3. 验证矩阵

| 层级 | 验证 |
| --- | --- |
| Codec | 缺失/未知/重复字段、恶意深度、超大输入、规范往返、异常不回显输入 |
| Catalog | 顺序无关 SHA-256、Descriptor/Schema/集合变化失效、稳定查找 |
| Validator | revision、重复/未知步骤、前向/循环边界、item 作用域、Secret、Schema、ForEach 与预算 |
| Runner | Sequence、顺序 ForEach、进度、失败停止、取消、运行期 Schema、Run 必然释放 |
| Document | 标题、目录、结构化命令、导入/导出、执行门控、关闭清理与脱敏状态 |
| Standalone | 两个 Fake Action，4 次调用，单 Run 释放，无窗口自检 |
| Package/Host | locked restore、零警告、两次确定性 ZIP、manifest/依赖扫描、隔离真实 Host 启动 |

权威入口：

```powershell
pwsh -NoProfile -File .\scripts\Test-WorkflowStudioG3.ps1 `
  -Configuration Release `
  -CandidateHostRoot C:\Path\To\CandidateHost\bin\Release\net10.0
```

权威机器摘要为 `artifacts/test-results/WorkflowStudioG3/summary.json`。该目录被 Git 忽略，只在全部阶段
成功后写入摘要。

最终实测结果：

| 证据 | 结果 |
| --- | ---: |
| 单元测试 | **43/43**，失败 0，跳过 0 |
| 行 / 分支覆盖率 | **85.57% / 76.52%** |
| Standalone 无窗口闭环 | **4 次调用 / 1 个 Run 完整释放** |
| 确定性插件构建 | **2 次，ZIP SHA-256 一致** |
| ZIP SHA-256 | `57B0627D8B30887C6D7BF032E9C549F97FC2672D0EAC021F23D48D1190CE663B` |
| ZIP 文件 / manifest | **4 个文件 / schema 2 / SDK [3.1.0, 4.0.0)** |
| 候选 Host | **退出码 0**；插件发现、组合、Document 与 Gateway 无错误 |

## 4. 非发布与回滚

摘要必须记录 `aiflow=false`、`windowsCi=false`、`releaseAcceptance=false`、`releaseGate=false` 和
`publishable=false`。候选 Host 验收只复制输出到新仓库的隔离结果目录，不修改 Host 源码或原始产物。

G3 的回滚单位是整个外部仓库。可以删除 `myavalonia-workflow-studio` 并用模板重新创建；Host、SDK、Build、
Templates 和原仓库文档均无需回滚。不得把 Studio 源码搬入 Host 解决方案来规避包或加载问题。
