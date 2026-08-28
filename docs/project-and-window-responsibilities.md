# 项目、SOLID 与窗口职责

## 三项目所有权

| 项目 | 负责 | 不负责 |
| --- | --- | --- |
| `WorkflowStudio.Plugin` | Module、非持久化 Document/View、定义、验证、Secret、风险、Runner 与 Document Command Target | 启动桌面程序、Provider Fake、Host UI 投影与内部治理 |
| `WorkflowStudio.Standalone` | Avalonia 启动、两个 Fake Action、Fake Gateway、开发期 Document Lifetime | 成为第二套业务实现、模拟授权/ALC、进入正式 ZIP |
| `WorkflowStudio.Tests` | Codec、revision、验证、Runner、Secret、Document 与注册测试 | 替代真实 Host 加载验收 |

## SOLID 落地

- **SRP**：Codec 只处理线格式，Catalog Projection 只生成目录快照，Validator 只产生问题，Resolver 只展开
  已验证引用，Runner 只编排调用，Document 只协调 UI 用例。
- **OCP**：Studio 从 Descriptor 目录发现新 Action；增加 Provider 不需要修改 Runner 或 UI 的调用协议。
- **LSP**：Standalone Fake 和测试替身完整实现 `IWorkflowActionGateway/IWorkflowActionRun`，调用方无需分支判断。
- **ISP**：定义、验证、风险、Secret、引用、目录和运行各自使用小接口，UI 不取得通用 ServiceProvider。
- **DIP**：核心依赖公开 SDK Gateway 与 Studio 自己的端口；只有最外层组合根选择真实 Host 或 Fake。

Workbench Command G7 沿用同一拆分：`PluginIds` 只管理稳定身份，Module 只声明不可变 Command/Menu/KeyBinding
描述符，`MainDocument` 只把三个已知 CommandId 适配到现有验证、`WorkflowRunSession` 和取消用例。Host 负责
Catalog、当前活动实例路由、菜单、快捷键和执行前重查；Studio 不取得 Host Provider、Dock 或 Avalonia 控件。

没有引入 Mediator、事件总线、Service Locator、工作流框架、通用管线或抽象工厂。使用的模式只有构造注入、
不可变描述符/快照、窄接口适配和实例状态通知，均直接服务生命周期或测试边界。

## 生命周期

Host 为每个 Document 建立独立 Scope，Secret Store、Validator、Resolver 和 Runner 随 Scope 创建。Document
观察 `IDocumentLifetime.ClosingToken`；关闭时先取消运行并清空会话状态，再由 Host 释放 Scope。

Workbench Target 的运行门闩同样属于 Document 实例。关闭开始后 Validate/Run/Cancel 全部 fail closed，正在运行的
调用通过既有 ClosingToken 链协作取消；状态事件逐 CommandId 发出，旧 Target 不会在 Document 关闭后继续影响 Host。

Standalone 复用 `AddWorkflowStudioServices()`，只补充 Fake Gateway 与 Fake Lifetime。窗口关闭使用同样的
“先发关闭信号、后释放 Scope、最后释放根容器”顺序。多个窗口或真实 Document 不共享 Secret 和运行状态。
