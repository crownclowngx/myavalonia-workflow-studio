# G0013 Studio 测试与门禁

2026-09-05 本地实施完成：全量 73 项通过、0 失败、0 跳过（本阶段新增 19 项）。
Debug 构建零警告零错误、全量格式只读检查、G3 自检及 Standalone 启动烟雾通过。

单元测试覆盖参数范围、冻结快照、重复配方独立文件名、绝对路径、16 项/32 步恢复预算、
输出版本不兼容、附加敏感字段和错误路径拒绝、关闭与迟到结果、文件选择取消及超限、
排序移除、逐项清理失败继续等情况。既有 54 项通用编辑器/Schema/Secret/Runner/注册回归继续执行。

跨插件集成位于 Fractal 独立测试工程，使用真实 Module 与 Handler；
本仓固定目录夹具逐字段对照真实注册，防止仅凭测试替身证明兼容。
真实面板在专用 Headless UI 线程验证编译绑定，并输出渲染 PNG。

```powershell
dotnet restore WorkflowStudio.slnx --locked-mode
dotnet build WorkflowStudio.slnx -c Debug --no-restore -warnaserror
dotnet test WorkflowStudio.slnx -c Debug --no-build --no-restore
dotnet format WorkflowStudio.slnx --verify-no-changes --no-restore
dotnet run --project src/WorkflowStudio.Standalone -c Debug --no-build -- --g3-self-test
```

三仓全量结果、指纹及烟雾见 [G0013 结果](../../../../myavalonia-fractal-art/docs/refactoring/G0013/result.md)。
本地 SDK Gateway 适配器不替代 Host 确认、真实授权、ALC、退出排空、ZIP 与发布验收。
