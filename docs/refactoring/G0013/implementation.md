# G0013 Studio 实施与交互

## 用户操作

1. 选择配方；可多选、上下排序、移除，也允许同一文件重复作为不同批项。
2. 选择已存在输出目录，调整 Blur/Bloom/Grain。默认值与 Fractal G0007 一致。
3. 点击生成并验证，再点击主工具栏“执行”。目录缺少所需插件或契约不兼容时显示原因。
4. 查看逐项后处理状态和临时源清理状态。文件输出不是 Fractal 实时效果，也不会修改原作品。
5. 中断后准备续跑未完成项，再点击执行；成功项保留，未完成项使用新输出名称。
6. 源过期、缺失或校验失败时，准备重新生成未完成项；执行时读取原路径下的当前配方，内容可能已变化。
7. 显式清理临时源经 Host 逐项确认，失败不阻断其他项。主取消按钮可结束整轮操作。
8. 关闭或明确放弃恢复会丢弃台账；输出保留，未释放源按生产者有效 marker 的 24 小时 TTL 回收。

## 定义与兼容

沿用 Definition v2。单张使用旧三个 Action，多张使用批量 Render、目录输出 ForEach、Release ForEach。
revision 来自同次真实目录捕获，不输出占位摘要。风险、Schema、敏感字段与 Artifact 输出协议必须兼容。
Builder 也检查无下游消费者的 ImageLab 最终输出，避免运行后才发现版本不兼容。

冻结快照包含步骤、顺序、Action 身份、参数、ForEach 与契约修订。编辑步骤后按普通工作流执行，
不把新运行套入旧恢复台账。旧台账仍明确属于上次内置示例。
同一已经运行的示例不能直接重复执行；重新准备续跑或生成新批次。

## SOLID 与生命周期

ArtWorkflowDefinitionBuilder 生成和验证；ArtWorkflowRecoverySession 保存窄的会话投影；
IArtWorkflowSourceValidator 只读检查可复用源；IWorkflowInvocationObserver 同步观察 Gateway 终态。
Runner 不保存整个 Action 输出；恢复只允许指定 Action 的预期字段，Secret 和任意附加输出不落盘。

ArtWorkflowPanel 适配表单与命令，文件选择通过独立 IArtWorkflowFilePicker；
View 附着时接入当前 StorageProvider，分离时清除窗口引用。批处理参数冻结，迟到选择取消后不再加入列表。
台账 Scoped，关闭立即清空，两个 Document 隔离。重新生成保留旧源供后续显式清理，
最多记录 256 个源身份；续跑最多 32 步，沿用现有总调用和时间预算。

不增加新工作台命令、快捷键、持久化格式或通用重试引擎。
Standalone 的现有 Fake 目录不包含 Fractal/ImageLab，所以该示例提示缺失能力；
这与真实 Host 中发现 Provider 的产品流程一致，并不伪装联调成功。
