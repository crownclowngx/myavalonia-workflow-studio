using System.Text.Json;
using WorkflowStudio.Features.Main;
using WorkflowStudio.Workflows;
using WorkflowStudio.Workflows.ArtWorkflow;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class G0013ArtWorkflowTests
{
    [Fact]
    public void 最终ImageLab输出版本变化即使没有下游引用也拒绝生成()
    {
        using var rig = new Rig();
        var original = rig.Action(ArtWorkflowDefinitionBuilder.Apply);
        var schema = System.Text.Json.Nodes.JsonNode.Parse(original.OutputSchema.GetRawText())!;
        schema["properties"]!["artifact"]!["properties"]!["version"]!["enum"] = new System.Text.Json.Nodes.JsonArray(2);
        var changed = new WorkflowActionDescriptor(original.Id, original.DisplayName, original.Description,
            original.InputSchema, JsonSerializer.SerializeToElement(schema), original.Risks, original.ConfirmationPolicy);
        rig.Gateway.Actions = rig.Gateway.Actions.Select(a => a.Id == changed.Id ? changed : a).ToArray();
        Assert.Throws<InvalidDataException>(() => rig.Builder.Create(rig.Catalog, [rig.Path], rig.Directory, new()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 效果有限范围校验(double sigma) => Assert.Throws<InvalidDataException>(() => new ArtWorkflowEffects(BlurSigma: sigma).Validate());

    [Fact]
    public void 输入快照不受原列表修改且重复配方拥有独立文件名()
    {
        using var rig = new Rig();
        var paths = new[] { rig.Path, rig.Path };
        var plan = rig.Builder.Create(rig.Catalog, paths, rig.Directory, new());
        paths[0] = "modified";
        Assert.Equal(rig.Path, plan.Items[0].RecipePath);
        Assert.Equal(2, plan.Items.Select(item => item.OutputPath).Distinct().Count());
        Assert.All(plan.Items, item => Assert.Matches("^fractal-[a-f0-9]{32}-[0-9]{2}$", item.ItemId));
    }

    [Theory]
    [InlineData("relative.json", false)]
    [InlineData("", false)]
    [InlineData("relative", true)]
    public void 非绝对输入输出路径被拒绝(string path, bool output)
    {
        using var rig = new Rig();
        Assert.Throws<InvalidDataException>(() => rig.Builder.Create(rig.Catalog,
            [output ? rig.Path : path], output ? path : rig.Directory, new()));
    }

    [Fact]
    public async Task 十六项恢复定义不超过现有三十二步骤预算()
    {
        using var rig = new Rig();
        var plan = rig.Attach(16);
        rig.Render(plan);
        rig.Recovery.End();
        var definition = await rig.Recovery.PrepareResumeAsync(rig.Catalog, default);
        Assert.Equal(32, definition.Steps.Count);
        Assert.Equal(16, rig.SourceValidator.Calls);
        Assert.True(rig.Validator.Validate(definition, rig.Catalog).IsValid);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("wrong-producer")]
    [InlineData("wrong-path")]
    public void 不可信结果不进入恢复成功记录或保存额外字段(string scenario)
    {
        using var rig = new Rig();
        var plan = rig.Attach(1); rig.Render(plan);
        var output = JsonSerializer.SerializeToElement(new
        {
            artifact = Rig.Artifact(ArtWorkflowDefinitionBuilder.ImageLabId, "persistent",
                scenario == "wrong-path" ? rig.Directory + "/TOP-SECRET-CANARY.png" : plan.Items[0].OutputPath),
            image = new { width = 64, height = 64 }
        });
        var node = System.Text.Json.Nodes.JsonNode.Parse(output.GetRawText())!;
        if (scenario == "extra") node["secret"] = "TOP-SECRET-CANARY";
        if (scenario == "wrong-producer") node["artifact"]!["producerPluginId"] = "myavalonia.plugin.fake";
        rig.Recovery.Started("process", null);
        rig.Recovery.Observe("process", null, rig.Action(ArtWorkflowDefinitionBuilder.Apply), WorkflowActionInvocationStatus.Succeeded, JsonSerializer.SerializeToElement(node));
        Assert.False(rig.Recovery.Items[0].Succeeded);
        Assert.Contains("需核对", rig.Recovery.Items[0].Processing);
        Assert.DoesNotContain("TOP-SECRET-CANARY", JsonSerializer.Serialize(rig.Recovery.Items));
        rig.Recovery.End();
    }

    [Fact]
    public void 关闭后迟到结果不恢复已清空的会话()
    {
        using var rig = new Rig();
        var plan = rig.Attach(1); rig.Render(plan);
        rig.Recovery.Started("process", null);
        rig.Recovery.Dispose();
        rig.Recovery.Observe("process", null, rig.Action(ArtWorkflowDefinitionBuilder.Apply), WorkflowActionInvocationStatus.Succeeded,
            JsonSerializer.SerializeToElement(new { secret = "CANARY" }));
        rig.Recovery.End();
        Assert.Empty(rig.Recovery.Items);
        Assert.Empty(rig.Recovery.PendingCleanup);
    }

    [Fact]
    public async Task 源校验期间放弃恢复会拒绝迟到的续跑定义()
    {
        using var rig = new Rig();
        var plan = rig.Attach(1); rig.Render(plan); rig.Recovery.End();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.SourceValidator.Wait = completion.Task;
        var pending = rig.Recovery.PrepareResumeAsync(rig.Catalog, default);
        rig.Recovery.Abandon(); completion.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Empty(rig.Recovery.Items);
    }

    [Fact]
    public async Task 文件选择期间不可重复触发且取消后的迟到选择不会加入列表()
    {
        using var rig = new Rig();
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new Picker { Pending = completion.Task };
        var panel = rig.Panel(picker);
        var task = panel.AddRecipesCommand.ExecuteAsync(null);
        Assert.True(panel.IsBusy);
        Assert.False(panel.CreateCommand.CanExecute(null));
        panel.Cancel(); completion.SetResult([rig.Path]); await task;
        Assert.Empty(panel.Recipes);
        Assert.False(panel.IsBusy);
    }

    [Fact]
    public async Task 文件选择超限保持原列表并支持排序移除()
    {
        using var rig = new Rig();
        var picker = new Picker { Pending = Task.FromResult<IReadOnlyList<string>>(Enumerable.Repeat(rig.Path, 17).ToArray()) };
        var panel = rig.Panel(picker);
        await panel.AddRecipesCommand.ExecuteAsync(null);
        Assert.Empty(panel.Recipes); Assert.Contains("16", panel.Status);
        picker.Pending = Task.FromResult<IReadOnlyList<string>>([rig.Path, rig.Path + "-2"]);
        await panel.AddRecipesCommand.ExecuteAsync(null);
        panel.SelectedIndex = 1; panel.MoveUpCommand.Execute(null);
        Assert.EndsWith("-2", panel.Recipes[0]);
        panel.RemoveRecipeCommand.Execute(null); Assert.Single(panel.Recipes);
        panel.Close(); Assert.Empty(panel.Recipes); Assert.Empty(rig.Recovery.Items);
        Assert.False(panel.CreateCommand.CanExecute(null));
    }

    [Fact]
    public void 批次结果数量身份或顺序不一致时不接收任何源()
    {
        using var rig = new Rig();
        var plan = rig.Attach(2);
        var result = JsonSerializer.SerializeToElement(new { results = new[] { new { itemId = "wrong", artifact = Rig.Artifact(ArtWorkflowDefinitionBuilder.FractalId, "run"), image = new { width = 64, height = 64 } } } });
        rig.Recovery.Observe("render-batch", null, rig.Action(ArtWorkflowDefinitionBuilder.Batch), WorkflowActionInvocationStatus.Succeeded, result);
        Assert.Empty(rig.Recovery.PendingCleanup);
        Assert.All(rig.Recovery.Items, item => Assert.Contains("未确认", item.Processing));
        rig.Recovery.End();
    }

    [Fact]
    public async Task 单项清理拒绝后仍尝试其余项()
    {
        using var rig = new Rig();
        var plan = rig.Attach(2); rig.Render(plan); rig.Recovery.End();
        var calls = 0;
        rig.Gateway.InvokeOverride = (_, _) => Task.FromResult(++calls == 1 ?
            new WorkflowActionInvocationResult(Guid.NewGuid(), WorkflowActionInvocationStatus.Failed, null, new("denied", "拒绝")) :
            new WorkflowActionInvocationResult(Guid.NewGuid(), WorkflowActionInvocationStatus.Succeeded, JsonSerializer.SerializeToElement(new { released = true }), null));
        var panel = rig.Panel(new Picker());
        await panel.CleanupCommand.ExecuteAsync(null);
        Assert.Equal(2, calls);
        Assert.Single(rig.Recovery.PendingCleanup);
    }

    private sealed class Rig : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "g0013-unit");
        public string Path => System.IO.Path.Combine(Directory, "recipe.json");
        public MutableGateway Gateway { get; } = new();
        public TestLifetime Lifetime { get; } = new();
        public SourceValidator SourceValidator { get; } = new();
        public WorkflowDefinitionValidator Validator { get; }
        public WorkflowActionCatalogProjection Projection { get; }
        public WorkflowActionCatalogSnapshot Catalog => Projection.Capture();
        public ArtWorkflowDefinitionBuilder Builder { get; }
        public ArtWorkflowRecoverySession Recovery { get; }
        private readonly WorkflowRunSession _runs;
        public Rig()
        {
            using var stream = typeof(Rig).Assembly.GetManifestResourceStream("WorkflowStudio.Tests.Fixtures.g0013-actions.json")!;
            using var json = JsonDocument.Parse(stream);
            Gateway.Actions = json.RootElement.EnumerateArray().Select(a =>
            {
                var id = a.GetProperty("id").GetString()!; var release = id == ArtWorkflowDefinitionBuilder.Release;
                return new WorkflowActionDescriptor(new(id), id, "测试夹具（集成测试对照真实注册）", a.GetProperty("inputSchema"), a.GetProperty("outputSchema"),
                    release ? WorkflowActionRiskFlags.DeletesLocalFiles : WorkflowActionRiskFlags.ReadsLocalFiles | WorkflowActionRiskFlags.WritesLocalFiles | WorkflowActionRiskFlags.LongRunning,
                    release ? WorkflowActionConfirmationPolicy.EveryInvocation : WorkflowActionConfirmationPolicy.OncePerRun);
            }).ToArray();
            var services = TestServiceFactory.Create(Gateway);
            Validator = services.Validator; Projection = services.Catalog;
            Builder = new(Validator); Recovery = new(Builder, SourceValidator);
            var runner = new WorkflowRunner(Gateway, Projection, Validator, services.Resolver, new WorkflowJsonSchemaValidator(), Recovery);
            _runs = new(runner, services.Secrets, Lifetime);
        }
        public ArtWorkflowPanel Panel(Picker picker) => new(Builder, Recovery, Projection, _runs, picker, Lifetime);
        public WorkflowActionDescriptor Action(string id) => Gateway.Actions.Single(action => action.Id.Value == id);
        public ArtWorkflowPlan Attach(int count)
        {
            var plan = Builder.Create(Catalog, Enumerable.Repeat(Path, count).ToArray(), Directory, new());
            Recovery.Attach(plan); Recovery.Begin(plan.Definition); return plan;
        }
        public void Render(ArtWorkflowPlan plan)
        {
            var output = plan.Items.Count == 1 ? JsonSerializer.SerializeToElement(new { artifact = Artifact(ArtWorkflowDefinitionBuilder.FractalId, "run"), image = new { width = 64, height = 64 } }) :
                JsonSerializer.SerializeToElement(new { results = plan.Items.Select(item => new { itemId = item.ItemId, artifact = Artifact(ArtWorkflowDefinitionBuilder.FractalId, "run"), image = new { width = 64, height = 64 } }) });
            var step = plan.Definition.Steps[0]; Recovery.Observe(step.Id, null, Action(step.ActionId.Value), WorkflowActionInvocationStatus.Succeeded, output);
        }
        public static object Artifact(string producer, string lifetime, string? path = null)
        {
            var id = Guid.NewGuid();
            return new
            {
                contract = ArtWorkflowArtifact.Contract,
                version = 1,
                producerPluginId = producer,
                producerOperationId = id.ToString("D"),
                lifetime,
                path = path ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MyAvaloniaManagement", "WorkflowArtifacts", producer, id.ToString("D"), "source.png"),
                mediaType = "image/png",
                byteLength = 100,
                sha256 = new string('A', 64)
            };
        }
        public void Dispose() { Recovery.Dispose(); _runs.Dispose(); Lifetime.Dispose(); }
    }
    private sealed class SourceValidator : IArtWorkflowSourceValidator
    {
        public int Calls;
        public Task Wait { get; set; } = Task.CompletedTask;
        public async Task ValidateAsync(ArtWorkflowArtifact artifact, CancellationToken token) { Calls++; await Wait; token.ThrowIfCancellationRequested(); }
    }
    private sealed class Picker : IArtWorkflowFilePicker
    {
        public Task<IReadOnlyList<string>> Pending { get; set; } = Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> PickRecipesAsync(CancellationToken token) => Pending;
        public Task<string?> PickDirectoryAsync(CancellationToken token) => Task.FromResult<string?>(null);
    }
}
