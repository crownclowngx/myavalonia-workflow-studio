using WorkflowStudio.Workflows;

namespace WorkflowStudio.Features.Main;

/// <summary>定义单个 Document 会话内的 Secret、运行、取消和关闭清理边界。</summary>
public interface IWorkflowRunSession : IDisposable
{
    void SetSecret(string name, string value);
    Task<WorkflowRunResult> RunAsync(
        WorkflowDefinitionV2 definition,
        IProgress<WorkflowRunProgress>? progress,
        CancellationToken cancellationToken);
    void Cancel();
    void Close();
}

/// <summary>拥有一个 Document 会话的 Secret、运行取消源和关闭清理。</summary>
/// <remarks>
/// 运行状态不进入 MainDocument 的业务算法；关闭时先取消在途任务，再清空 Secret。
/// 多次 Close/Dispose 安全，便于 Host ClosingToken 与 Document Dispose 竞争调用。
/// </remarks>
public sealed class WorkflowRunSession(
    IWorkflowRunner runner,
    ISessionSecretStore secrets,
    MyAvaloniaManagement.PluginSdk.IDocumentLifetime lifetime) : IWorkflowRunSession
{
    private CancellationTokenSource? _current;
    private bool _disposed;

    public void SetSecret(string name, string value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        secrets.Set(name, value);
    }

    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinitionV2 definition,
        IProgress<WorkflowRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is not null)
        {
            throw new InvalidOperationException("当前 Document 已有工作流正在执行。");
        }
        _current = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.ClosingToken, cancellationToken);
        try
        {
            return await runner.RunAsync(definition, progress, _current.Token);
        }
        finally
        {
            _current.Dispose();
            _current = null;
        }
    }

    public void Cancel() => _current?.Cancel();

    public void Close()
    {
        _current?.Cancel();
        secrets.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Close();
        if (secrets is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _disposed = true;
    }
}
