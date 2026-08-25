using System.Collections.ObjectModel;
using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Standalone;

/// <summary>
/// Standalone 专用 Fake Gateway。它只实现公开 SDK 端口，不复制 Host 的目录所有权、授权或 ALC；
/// 因此适合预览 UI 和验证 Consumer 闭环，但不能替代真实 Host 验收。
/// </summary>
internal sealed class FakeWorkflowActionGateway : IWorkflowActionGateway
{
    internal const string GenerateId =
        "myavalonia.plugin.workflow-studio-fake.workflow.generate-items";
    internal const string FormatId =
        "myavalonia.plugin.workflow-studio-fake.workflow.format-item";

    private readonly IReadOnlyList<WorkflowActionDescriptor> _actions =
        new ReadOnlyCollection<WorkflowActionDescriptor>(CreateDescriptors());
    private int _invocations;
    private int _disposedRuns;

    internal int Invocations => Volatile.Read(ref _invocations);
    internal int DisposedRuns => Volatile.Read(ref _disposedRuns);

    public IReadOnlyList<WorkflowActionDescriptor> GetAvailableActions() => _actions;

    public IWorkflowActionRun CreateRun() => new FakeRun(this);

    private static WorkflowActionDescriptor[] CreateDescriptors()
    {
        using var generateInput = JsonDocument.Parse(
            """{"type":"object","properties":{"count":{"type":"integer","minimum":1,"maximum":10},"prefix":{"type":"string","minLength":1,"maxLength":32}},"required":["count","prefix"],"additionalProperties":false}""");
        using var generateOutput = JsonDocument.Parse(
            """{"type":"object","properties":{"items":{"type":"array","maxItems":10,"items":{"type":"object","properties":{"index":{"type":"integer"},"value":{"type":"string","maxLength":64}},"required":["index","value"],"additionalProperties":false}}},"required":["items"],"additionalProperties":false}""");
        using var formatInput = JsonDocument.Parse(
            """{"type":"object","properties":{"value":{"type":"string","minLength":1,"maxLength":64},"secret":{"type":"string","minLength":1,"maxLength":128}},"required":["value","secret"],"additionalProperties":false}""");
        using var formatOutput = JsonDocument.Parse(
            """{"type":"object","properties":{"formatted":{"type":"string","maxLength":96},"secretAccepted":{"type":"boolean"}},"required":["formatted","secretAccepted"],"additionalProperties":false}""");
        return
        [
            new WorkflowActionDescriptor(
                new WorkflowActionId(GenerateId),
                "生成有界列表",
                "生成最多十个带序号的测试项，用于验证前序输出和 ForEach。",
                generateInput.RootElement,
                generateOutput.RootElement,
                WorkflowActionRiskFlags.None,
                WorkflowActionConfirmationPolicy.Never),
            new WorkflowActionDescriptor(
                new WorkflowActionId(FormatId),
                "处理单项",
                "顺序处理一个测试项并确认收到会话 Secret，但不会回显 Secret。",
                formatInput.RootElement,
                formatOutput.RootElement,
                WorkflowActionRiskFlags.HandlesSecret,
                WorkflowActionConfirmationPolicy.OncePerRun,
                ["/secret"]),
        ];
    }

    private sealed class FakeRun(FakeWorkflowActionGateway owner) : IWorkflowActionRun
    {
        private readonly CancellationTokenSource _closing = new();
        private int _disposed;

        public async Task<WorkflowActionInvocationResult> InvokeAsync(
            WorkflowActionInvocationRequest request,
            IProgress<WorkflowActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentNullException.ThrowIfNull(request);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _closing.Token);
            var invocationId = Guid.NewGuid();
            Interlocked.Increment(ref owner._invocations);
            try
            {
                progress?.Report(new WorkflowActionProgress("started", 0, "Fake Action 已开始。"));
                await Task.Delay(20, linked.Token);
                JsonElement output;
                if (request.ActionId.Value == GenerateId)
                {
                    var count = request.Arguments.GetProperty("count").GetInt32();
                    var prefix = request.Arguments.GetProperty("prefix").GetString()!;
                    output = JsonSerializer.SerializeToElement(new
                    {
                        items = Enumerable.Range(1, count)
                            .Select(index => new { index, value = prefix + index }).ToArray(),
                    });
                }
                else if (request.ActionId.Value == FormatId)
                {
                    var value = request.Arguments.GetProperty("value").GetString()!;
                    var secret = request.Arguments.GetProperty("secret").GetString()!;
                    if (value == "fail")
                    {
                        return new WorkflowActionInvocationResult(
                            invocationId,
                            WorkflowActionInvocationStatus.Failed,
                            output: null,
                            new WorkflowActionFailure("fake.failed", "Fake Action 按测试输入失败。"));
                    }
                    output = JsonSerializer.SerializeToElement(new
                    {
                        formatted = value + "-已处理",
                        secretAccepted = secret.Length > 0,
                    });
                }
                else
                {
                    return new WorkflowActionInvocationResult(
                        invocationId,
                        WorkflowActionInvocationStatus.Unavailable,
                        output: null,
                        new WorkflowActionFailure("fake.unknown", "Fake Action 不存在。"));
                }
                progress?.Report(new WorkflowActionProgress("completed", 100, "Fake Action 已完成。"));
                return new WorkflowActionInvocationResult(
                    invocationId,
                    WorkflowActionInvocationStatus.Succeeded,
                    output,
                    failure: null);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                return new WorkflowActionInvocationResult(
                    invocationId,
                    WorkflowActionInvocationStatus.Cancelled,
                    output: null,
                    failure: null);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _closing.Cancel();
                _closing.Dispose();
                Interlocked.Increment(ref owner._disposedRuns);
            }
            return ValueTask.CompletedTask;
        }
    }
}
