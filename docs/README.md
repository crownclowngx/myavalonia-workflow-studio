# Workflow Studio 文档入口

> 状态：Workflow Action G3.1、Workbench Command G7 与 G10 本地封板已完成；当前为非发布基线。

本仓库位于 Host 仓库之外，只通过精确版本的 Core/UI SDK `3.3.0`、Workflow SDK `1.0.0` 与 Build `1.1.2`
相交。`WorkflowStudio.slnx` 不引用 Host 源码；Host 只消费构建生成的真实插件 ZIP。

| 文档 | 说明 |
| --- | --- |
| [定义 v2 与执行语义](workflow-definition-v2.md) | 当前私有线格式、双 revision、引用、验证与运行失败边界 |
| [定义 v1 历史说明](workflow-definition-v1.md) | G3 历史格式；当前 Codec 明确拒绝 |
| [Workflow Action 接入](workflow-actions.md) | Consumer 注册、Standalone Fake 与真实 caller-bound Gateway |
| [G0013 Fractal → ImageLab](refactoring/G0013/README.md) | 内置完整定义、部分成功续跑、重新生成与显式清理 |
| [Workbench Command 三命令设计](workbench-commands.md) | Validate/Run/Cancel 身份、状态、执行链、SOLID 与生命周期 |
| [项目和窗口职责](project-and-window-responsibilities.md) | 三项目所有权、SOLID 划分与关闭顺序 |
| [部署与非发布验收](deployment-and-release.md) | 本地 ZIP、真实 Host 和 G3.1/G7/G10 专项门禁 |
| [G3 专用实施记录](plan-history/workflow-action/g3-workflow-studio-fake-action-loop.md) | 模板事实、测试矩阵、机器摘要与回滚边界 |
| [G3.1 专用实施记录](plan-history/workflow-action/g3.1-protocol-consistency-and-reference-safety.md) | 共享 SDK、v2 硬切、静态引用与门禁证据 |
| [Workbench Command G7 专用记录](plan-history/workbench-command/g7-workflow-studio-three-real-commands.md) | 三条真实命令、真实包集成、覆盖率与非发布边界 |
| [Workbench Command G10 本地封板](plan-history/workbench-command/g10-workflow-studio-local-sealing.md) | G7 事实复核、跨仓组合、SOLID 与非发布边界 |

## 当前能力

- 从 Gateway 当前目录添加、删除和排序步骤；
- 通过内置 Fractal → ImageLab 示例生成单张或 1–16 项批量定义，并在当前会话恢复未完成项；
- 编辑 JSON 常量、前序输出引用、ForEach item 引用和会话 Secret 引用；
- 严格导入与规范导出定义 v2，并拒绝 v1、未知字段、重复字段和混合版本；
- 在执行前检查 Contract/Presentation revision、引用路径保证、Schema 可赋值、Secret、风险和预算；
- 在一个 caller-bound Run 内顺序执行普通步骤与有限 ForEach，失败或取消立即停止；
- 关闭 Document 时取消运行并丢弃定义、运行中间输出和 Secret。
- 通过 Host-owned Tools 菜单和 `F6`/`F5`/`Shift+F5` 执行当前 Studio Document 的验证、运行和取消。

当前版本不包含 AI 规划、脚本、任意表达式、并行写操作、自动补偿、持久化、重启恢复、自动重试或 AIFLOW。
