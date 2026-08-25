using Microsoft.Extensions.DependencyInjection;
using WorkflowStudio.Workflows;

namespace WorkflowStudio.Plugin;

public static class WorkflowStudioServices
{
    /// <summary>
    /// 登记 Workflow Studio 的业务服务。这里是插件与 Standalone 共用的唯一业务组合入口：
    /// Host 只额外提供 caller-bound Gateway 和 Document Lifetime，Standalone 则提供边界清晰的 Fake。
    /// </summary>
    public static IServiceCollection AddWorkflowStudioServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Codec、Schema 校验和风险摘要器均无会话状态，使用 Singleton 可以明确表达其可复用性。
        services.AddSingleton<IWorkflowDefinitionCodec, WorkflowDefinitionCodec>();
        services.AddSingleton<IWorkflowJsonSchemaValidator, WorkflowJsonSchemaValidator>();
        services.AddSingleton<IWorkflowRiskSummaryBuilder, WorkflowRiskSummaryBuilder>();

        // 目录、Secret 和 Runner 都属于一次 Document Scope。这样两个 Studio 文档不会共享
        // Secret、取消源或运行中间结果，也与 Host 的 Document 所有权模型保持一致。
        services.AddScoped<IWorkflowActionCatalogProjection, WorkflowActionCatalogProjection>();
        services.AddScoped<IWorkflowDefinitionValidator, WorkflowDefinitionValidator>();
        services.AddScoped<IWorkflowReferenceResolver, WorkflowReferenceResolver>();
        services.AddScoped<ISessionSecretStore, SessionSecretStore>();
        services.AddScoped<IWorkflowRunner, WorkflowRunner>();
        return services;
    }
}
