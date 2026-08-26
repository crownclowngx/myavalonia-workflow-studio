using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.Workflow;
using WorkflowStudio.Workflows;

namespace WorkflowStudio.Tests;

internal static class TestActions
{
    internal const string GenerateId = "myavalonia.plugin.test-provider.workflow.generate-items";
    internal const string FormatId = "myavalonia.plugin.test-provider.workflow.format-item";

    internal static WorkflowActionDescriptor Generate(string displayName = "生成") => Descriptor(
        GenerateId,
        displayName,
        "生成测试项。",
        """{"type":"object","properties":{"count":{"type":"integer","minimum":1,"maximum":3},"prefix":{"type":"string","minLength":1,"maxLength":16}},"required":["count","prefix"],"additionalProperties":false}""",
        """{"type":"object","properties":{"items":{"type":"array","maxItems":3,"items":{"type":"object","properties":{"value":{"type":"string","minLength":1,"maxLength":32}},"required":["value"],"additionalProperties":false}}},"required":["items"],"additionalProperties":false}""",
        WorkflowActionRiskFlags.None,
        WorkflowActionConfirmationPolicy.Never);

    internal static WorkflowActionDescriptor Format() => Descriptor(
        FormatId,
        "格式化",
        "处理测试项。",
        """{"type":"object","properties":{"value":{"type":"string","minLength":1,"maxLength":32},"secret":{"type":"string","minLength":1,"maxLength":64}},"required":["value","secret"],"additionalProperties":false}""",
        """{"type":"object","properties":{"formatted":{"type":"string","maxLength":64}},"required":["formatted"],"additionalProperties":false}""",
        WorkflowActionRiskFlags.HandlesSecret,
        WorkflowActionConfirmationPolicy.OncePerRun,
        ["/secret"]);

    internal static WorkflowActionDescriptor Descriptor(
        string id,
        string displayName,
        string description,
        string input,
        string output,
        WorkflowActionRiskFlags risks,
        WorkflowActionConfirmationPolicy confirmation,
        IReadOnlyList<string>? sensitive = null)
    {
        using var inputDocument = JsonDocument.Parse(input);
        using var outputDocument = JsonDocument.Parse(output);
        return new WorkflowActionDescriptor(
            new WorkflowActionId(id), displayName, description,
            inputDocument.RootElement, outputDocument.RootElement,
            risks, confirmation, sensitive);
    }

    internal static WorkflowDefinitionV2 ValidDefinition(WorkflowActionCatalogSnapshot catalog) => new(
        2,
        catalog.ContractRevision,
        catalog.PresentationRevision,
        "测试闭环",
        [
            new WorkflowStepDefinition(
                "generate",
                new WorkflowActionId(GenerateId),
                JsonSerializer.SerializeToElement(new { count = 2, prefix = "item" })),
            new WorkflowStepDefinition(
                "format",
                new WorkflowActionId(FormatId),
                JsonSerializer.SerializeToElement(new
                {
                    value = "${item.value}",
                    secret = "${secret.session-key}",
                }),
                "${generate.result.items}"),
        ]);
}

internal sealed class MutableGateway : IWorkflowActionGateway
{
    internal IReadOnlyList<WorkflowActionDescriptor> Actions { get; set; } =
        [TestActions.Generate(), TestActions.Format()];
    internal List<WorkflowActionInvocationRequest> Requests { get; } = [];
    internal int RunsCreated { get; private set; }
    internal int RunsDisposed { get; private set; }
    internal Func<WorkflowActionInvocationRequest, CancellationToken, Task<WorkflowActionInvocationResult>>?
        InvokeOverride
    { get; set; }

    public IReadOnlyList<WorkflowActionDescriptor> GetAvailableActions() => Actions;

    public IWorkflowActionRun CreateRun()
    {
        RunsCreated++;
        return new Run(this);
    }

    private sealed class Run(MutableGateway owner) : IWorkflowActionRun
    {
        private int _disposed;

        public async Task<WorkflowActionInvocationResult> InvokeAsync(
            WorkflowActionInvocationRequest request,
            IProgress<WorkflowActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            owner.Requests.Add(request);
            progress?.Report(new WorkflowActionProgress("test", 50, "受控测试进度"));
            if (owner.InvokeOverride is not null)
            {
                return await owner.InvokeOverride(request, cancellationToken);
            }
            var id = Guid.NewGuid();
            if (request.ActionId.Value == TestActions.GenerateId)
            {
                var count = request.Arguments.GetProperty("count").GetInt32();
                return Success(id, JsonSerializer.SerializeToElement(new
                {
                    items = Enumerable.Range(1, count).Select(index => new { value = "item" + index }).ToArray(),
                }));
            }
            var value = request.Arguments.GetProperty("value").GetString();
            return Success(id, JsonSerializer.SerializeToElement(new { formatted = value + "-ok" }));
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.RunsDisposed++;
            }
            return ValueTask.CompletedTask;
        }

        private static WorkflowActionInvocationResult Success(Guid id, JsonElement output) =>
            new(id, WorkflowActionInvocationStatus.Succeeded, output, failure: null);
    }
}

internal sealed class TestLifetime : IDocumentLifetime, IDisposable
{
    private readonly CancellationTokenSource _source = new();
    public CancellationToken ClosingToken => _source.Token;
    public bool IsClosing => _source.IsCancellationRequested;
    internal void Close() => _source.Cancel();
    public void Dispose() => _source.Dispose();
}

internal static class TestServiceFactory
{
    internal static (
        WorkflowActionCatalogProjection Catalog,
        WorkflowDefinitionCodec Codec,
        WorkflowDefinitionValidator Validator,
        WorkflowReferenceResolver Resolver,
        WorkflowRunner Runner,
        SessionSecretStore Secrets) Create(MutableGateway gateway)
    {
        var secrets = new SessionSecretStore();
        var catalog = new WorkflowActionCatalogProjection(gateway);
        var schema = new WorkflowJsonSchemaValidator();
        var shared = new WorkflowSchemaValidator();
        var validator = new WorkflowDefinitionValidator(
            schema, secrets, new WorkflowReferenceTypeSystem(), shared);
        var resolver = new WorkflowReferenceResolver(secrets);
        var runner = new WorkflowRunner(gateway, catalog, validator, resolver, schema);
        return (catalog, new WorkflowDefinitionCodec(), validator, resolver, runner, secrets);
    }
}
