# Workflow Studio 文档入口

> 状态：Workflow Action G3 已实现；当前为本地开发与候选 Host 验收基线，不具备发布资格。

本仓库位于 Host 仓库之外，只通过 NuGet.org 上精确版本的 Core/UI SDK `3.1.0` 与 Build `1.1.2`
相交。`WorkflowStudio.slnx` 不引用 Host 源码；候选 Host 只消费构建生成的真实插件 ZIP。

| 文档 | 说明 |
| --- | --- |
| [定义 v1 与执行语义](workflow-definition-v1.md) | 私有线格式、引用、预算、验证、Runner 与 Secret 边界 |
| [Workflow Action 接入](workflow-actions.md) | Consumer 注册、Standalone Fake 与真实 caller-bound Gateway |
| [项目和窗口职责](project-and-window-responsibilities.md) | 三项目所有权、SOLID 划分与关闭顺序 |
| [部署与非发布验收](deployment-and-release.md) | 本地 ZIP、候选 Host 和 G3 专项门禁 |
| [G3 专用实施记录](plan-history/workflow-action/g3-workflow-studio-fake-action-loop.md) | 模板事实、测试矩阵、机器摘要与回滚边界 |

## 当前能力

- 从 Gateway 当前目录添加、删除和排序步骤；
- 编辑 JSON 常量、前序输出引用、ForEach item 引用和会话 Secret 引用；
- 严格导入与规范导出定义 v1；
- 在执行前检查目录 revision、Action、Schema、引用、Secret、风险和预算；
- 在一个 caller-bound Run 内顺序执行普通步骤与有限 ForEach，失败或取消立即停止；
- 关闭 Document 时取消运行并丢弃定义、运行中间输出和 Secret。

G3 不包含 AI 规划、脚本、任意表达式、并行写操作、自动补偿、持久化、重启恢复或自动重试。
