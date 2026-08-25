using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Features.Main;
using WorkflowStudio.Plugin;

namespace WorkflowStudio.Standalone;

internal sealed class StandaloneDocumentLifetime : IDocumentLifetime, IDisposable
{
    private readonly CancellationTokenSource _closing = new();
    public CancellationToken ClosingToken => _closing.Token;
    public bool IsClosing => _closing.IsCancellationRequested;
    internal void Close() => _closing.Cancel();
    public void Dispose() => _closing.Dispose();
}
/// <summary>
/// Standalone 只替换 Host 明确拥有的两个端口，其他业务服务继续复用 Plugin 的组合入口。
/// 组合对象拥有根容器和 Document Scope，关闭窗口时按“先发关闭令牌、后释放 Scope”清理。
/// </summary>
internal sealed class StandaloneComposition : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private bool _disposed;

    private StandaloneComposition(
        ServiceProvider provider,
        IServiceScope scope,
        MainDocument document,
        FakeWorkflowActionGateway gateway,
        StandaloneDocumentLifetime lifetime)
    {
        _provider = provider;
        _scope = scope;
        Document = document;
        Gateway = gateway;
        Lifetime = lifetime;
    }

    internal MainDocument Document { get; }
    internal FakeWorkflowActionGateway Gateway { get; }
    internal StandaloneDocumentLifetime Lifetime { get; }

    internal static StandaloneComposition Create()
    {
        var services = new ServiceCollection();
        services.AddWorkflowStudioServices();
        services.AddSingleton<FakeWorkflowActionGateway>();
        services.AddSingleton<IWorkflowActionGateway>(provider =>
            provider.GetRequiredService<FakeWorkflowActionGateway>());
        services.AddScoped<StandaloneDocumentLifetime>();
        services.AddScoped<IDocumentLifetime>(provider =>
            provider.GetRequiredService<StandaloneDocumentLifetime>());
        services.AddScoped<MainDocument>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var scope = provider.CreateScope();
        try
        {
            return new StandaloneComposition(
                provider,
                scope,
                scope.ServiceProvider.GetRequiredService<MainDocument>(),
                provider.GetRequiredService<FakeWorkflowActionGateway>(),
                scope.ServiceProvider.GetRequiredService<StandaloneDocumentLifetime>());
        }
        catch
        {
            scope.Dispose();
            provider.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Lifetime.Close();
        _scope.Dispose();
        _provider.Dispose();
    }
}
