using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;

namespace WorkflowStudio.Workflows.ArtWorkflow;

public sealed record ArtWorkflowItemStatus(string ItemId, string RecipePath, string OutputPath,
    bool Succeeded, string Processing, string Cleanup, IReadOnlyList<string> UncertainOutputs);

/// <summary>
/// 当前 Document 的恢复台账。只接受由本会话生成并逐字段匹配的定义，逐项记录 Host 终态。
/// UI 获取副本，Runner 通过同步观察端口提交；锁只保护小型元数据，文件检查不在锁内执行。
/// 台账不落盘，不保存完整 Action 输出或 Secret，不直接删除任何 Provider 文件。
/// </summary>
public sealed class ArtWorkflowRecoverySession(ArtWorkflowDefinitionBuilder builder, IArtWorkflowSourceValidator sourceValidator)
    : IWorkflowInvocationObserver, IDisposable
{
    private readonly object _sync = new();
    private readonly List<Item> _items = [];
    private readonly Dictionary<Guid, OwnedSource> _sources = [];
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private WorkflowDefinitionV2? _expected;
    private ArtWorkflowEffects? _effects;
    private string _directory = string.Empty;
    private bool _active;
    private bool _consumed;
    private bool _disposed;
    private int _generation;

    public IReadOnlyList<ArtWorkflowItemStatus> Items
    {
        get
        {
            lock (_sync) return _items.Select(item => new ArtWorkflowItemStatus(item.Recipe.ItemId,
            item.Recipe.RecipePath, item.OutputPath, item.Succeeded, item.Processing,
            item.Source is null ? "尚无临时文件" : _sources[item.Source.OperationId].Status,
            Array.AsReadOnly(item.Uncertain.ToArray()))).ToArray();
        }
    }
    public IReadOnlyList<Guid> PendingCleanup
    {
        get { lock (_sync) return _sources.Where(pair => !pair.Value.Released).Select(pair => pair.Key).ToArray(); }
    }
    public bool HasItems { get { lock (_sync) return _items.Count != 0; } }

    public void Attach(ArtWorkflowPlan plan)
    {
        lock (_sync)
        {
            EnsureIdle();
            if (_sources.Values.Any(source => !source.Released))
                throw new InvalidOperationException("上一批仍有临时文件，请先清理或明确放弃恢复。");
            _items.Clear();
            _sources.Clear();
            _items.AddRange(plan.Items.Select(recipe => new Item(recipe)));
            _effects = plan.Effects;
            _directory = plan.OutputDirectory;
            BindInitial(plan.Definition, Enumerable.Range(0, _items.Count).ToArray());
        }
    }

    public void Begin(WorkflowDefinitionV2 definition)
    {
        lock (_sync)
        {
            _active = false;
            if (_disposed || _expected is null || !SameDefinition(_expected, definition)) return;
            if (_consumed) throw new InvalidOperationException("该示例已运行，请使用续跑、重新生成或清理入口，避免重复生成输出。");
            _active = true;
            _consumed = true;
        }
    }

    public void Started(string stepId, int? itemIndex)
    {
        lock (_sync)
        {
            if (!_active || !_bindings.TryGetValue(stepId, out var binding) || binding.Kind != "process") return;
            var item = _items[Select(binding, itemIndex)];
            item.Started = true;
            item.Processing = "处理中";
        }
    }

    public void Observe(string stepId, int? itemIndex, WorkflowActionDescriptor descriptor,
        WorkflowActionInvocationStatus status, JsonElement? output)
    {
        lock (_sync)
        {
            if (!_active || !_bindings.TryGetValue(stepId, out var binding) ||
                descriptor.Id.Value != binding.ActionId || descriptor.SensitiveInputPointers.Count != 0) return;
            try
            {
                if (status != WorkflowActionInvocationStatus.Succeeded || output is null)
                {
                    MarkFailure(binding, itemIndex);
                    return;
                }
                var value = output.Value;
                if (binding.Kind == "render")
                {
                    var parsed = new List<(int Index, ArtWorkflowArtifact Artifact)>();
                    if (binding.ActionId == ArtWorkflowDefinitionBuilder.Batch)
                    {
                        ArtWorkflowArtifact.RequireFields(value, "results");
                        var results = value.GetProperty("results");
                        if (results.GetArrayLength() != binding.Indices.Length) throw new InvalidDataException("批次数量不匹配。");
                        var offset = 0;
                        foreach (var result in results.EnumerateArray())
                        {
                            var index = binding.Indices[offset++];
                            ArtWorkflowArtifact.RequireFields(result, "itemId", "artifact", "image");
                            if (result.GetProperty("itemId").GetString() != _items[index].Recipe.ItemId)
                                throw new InvalidDataException("批次身份或顺序不匹配。");
                            ArtWorkflowArtifact.ValidateImage(result.GetProperty("image"));
                            parsed.Add((index, ArtWorkflowArtifact.Parse(result.GetProperty("artifact"), ArtWorkflowDefinitionBuilder.FractalId, "run")));
                        }
                    }
                    else
                    {
                        ArtWorkflowArtifact.RequireFields(value, "artifact", "image");
                        ArtWorkflowArtifact.ValidateImage(value.GetProperty("image"));
                        parsed.Add((binding.Indices[0], ArtWorkflowArtifact.Parse(value.GetProperty("artifact"), ArtWorkflowDefinitionBuilder.FractalId, "run")));
                    }
                    if (parsed.Select(p => p.Artifact.OperationId).Distinct().Count() != parsed.Count ||
                        parsed.Any(p => _sources.ContainsKey(p.Artifact.OperationId)))
                        throw new InvalidDataException("批次重复使用已有操作身份。");
                    foreach (var (index, artifact) in parsed)
                    {
                        _items[index].Source = artifact;
                        _sources.Add(artifact.OperationId, new(artifact));
                    }
                }
                else if (binding.Kind == "process")
                {
                    var item = _items[Select(binding, itemIndex)];
                    ArtWorkflowArtifact.RequireFields(value, "artifact", "image");
                    ArtWorkflowArtifact.ValidateImage(value.GetProperty("image"));
                    _ = ArtWorkflowArtifact.Parse(value.GetProperty("artifact"), ArtWorkflowDefinitionBuilder.ImageLabId, "persistent", item.OutputPath);
                    item.Succeeded = true;
                    item.Started = false;
                    item.Processing = "已成功（Host 已确认）";
                }
                else
                {
                    var artifact = SourceFor(binding, itemIndex);
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("released", out var released) ||
                        released.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("释放结果无效。");
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                        if (!names.Add(property.Name) || property.Name is not ("released" or "warningCode")) throw new InvalidDataException("释放结果字段无效。");
                    var owned = _sources[artifact.OperationId];
                    owned.Released = released.GetBoolean();
                    owned.Status = owned.Released ? "已释放" : "延迟清理，请重试或等待 TTL";
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException)
            {
                // 不保存外部异常正文。协议异常使本项保持未确认，不能因为 Gateway 成功就接受错误路径。
                MarkFailure(binding, itemIndex);
            }
        }
    }

    public void End()
    {
        lock (_sync)
        {
            if (_active)
                foreach (var item in _items.Where(item => item.Started)) Uncertain(item);
            _active = false;
        }
    }

    public async Task<WorkflowDefinitionV2> PrepareResumeAsync(WorkflowActionCatalogSnapshot catalog, CancellationToken token)
    {
        (int Index, ArtWorkflowArtifact Artifact)[] pending;
        int generation;
        lock (_sync)
        {
            EnsureIdle();
            generation = _generation;
            pending = _items.Select((item, index) => (item, index)).Where(p => !p.item.Succeeded)
                .Select(p => (p.index, p.item.Source ?? throw new InvalidDataException("尚无可复用源文件，请重新生成未完成项。"))).ToArray();
            if (pending.Length == 0) throw new InvalidOperationException("后处理已全部成功；若仍有临时文件，请单独清理。");
            if (pending.Any(p => _sources[p.Artifact.OperationId].Released))
                throw new InvalidDataException("源文件已释放，请重新生成未完成项。");
        }
        foreach (var (_, artifact) in pending)
        {
            try { await sourceValidator.ValidateAsync(artifact, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            { throw new InvalidDataException("源文件缺失、过期或校验失败，请重新生成未完成项。", ex); }
        }
        lock (_sync)
        {
            EnsureIdle();
            token.ThrowIfCancellationRequested();
            if (_generation != generation) throw new InvalidOperationException("恢复会话已变化，请重新操作。");
            var steps = new List<WorkflowStepDefinition>();
            var bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
            var paths = new Dictionary<int, string>();
            foreach (var (index, artifact) in pending)
            {
                var path = Path.Combine(_directory, $"fractal-{Guid.NewGuid():N}-{index + 1:D2}.png");
                var id = $"retry-{index}";
                paths.Add(index, path);
                steps.Add(ArtWorkflowDefinitionBuilder.Step(id, ArtWorkflowDefinitionBuilder.Apply,
                    ArtWorkflowDefinitionBuilder.ApplyArguments(artifact.ToJson(), _effects!, path)));
                bindings.Add(id, new("process", ArtWorkflowDefinitionBuilder.Apply, [index]));
            }
            foreach (var pair in _items.Select((item, index) => (item, index)).Where(p => p.item.Source is not null && !_sources[p.item.Source.OperationId].Released))
            {
                var id = $"release-{pair.index}";
                steps.Add(ArtWorkflowDefinitionBuilder.Step(id, ArtWorkflowDefinitionBuilder.Release, new { artifact = pair.item.Source!.ToJson() }));
                bindings.Add(id, new("release", ArtWorkflowDefinitionBuilder.Release, [pair.index]));
            }
            var definition = builder.Validate(catalog, steps);
            foreach (var (index, path) in paths) { _items[index].OutputPath = path; _items[index].Processing = "等待续跑"; }
            Arm(definition, bindings);
            return definition;
        }
    }

    public WorkflowDefinitionV2 PrepareRegenerate(WorkflowActionCatalogSnapshot catalog)
    {
        lock (_sync)
        {
            EnsureIdle();
            var indices = _items.Select((item, index) => (item, index)).Where(p => !p.item.Succeeded).Select(p => p.index).ToArray();
            if (indices.Length == 0) throw new InvalidOperationException("没有未完成项。");
            if (_sources.Count + indices.Length > 256) throw new InvalidOperationException("会话恢复预算已满，请清理并结束本批次。");
            var plan = builder.Create(catalog, indices.Select(i => _items[i].Recipe.RecipePath).ToArray(), _directory, _effects!);
            for (var i = 0; i < indices.Length; i++)
            {
                var item = _items[indices[i]];
                item.Recipe = plan.Items[i];
                item.OutputPath = plan.Items[i].OutputPath;
                item.Source = null;
                item.Processing = "等待重新生成（读取当前配方）";
            }
            // 被替换的源仍留在 _sources 中供显式清理，不能在重新生成时丢失所有权记录。
            BindInitial(plan.Definition, indices);
            return plan.Definition;
        }
    }

    public WorkflowDefinitionV2 PrepareCleanup(WorkflowActionCatalogSnapshot catalog, Guid operationId)
    {
        lock (_sync)
        {
            EnsureIdle();
            var source = _sources[operationId];
            var definition = builder.Validate(catalog, [ArtWorkflowDefinitionBuilder.Step("cleanup", ArtWorkflowDefinitionBuilder.Release,
                new { artifact = source.Artifact.ToJson() })]);
            Arm(definition, new() { ["cleanup"] = new("release", ArtWorkflowDefinitionBuilder.Release, [], operationId) });
            return definition;
        }
    }

    public void Abandon()
    {
        lock (_sync)
        {
            EnsureIdle();
            Clear();
        }
    }
    public void Dispose() { lock (_sync) { _disposed = true; Clear(); } }

    private void BindInitial(WorkflowDefinitionV2 definition, int[] indices) => Arm(definition,
        definition.Steps.ToDictionary(s => s.Id, s => new Binding(s.Id.StartsWith("render", StringComparison.Ordinal) ? "render" :
            s.Id == "process" ? "process" : "release", s.ActionId.Value, indices), StringComparer.Ordinal));
    private void Arm(WorkflowDefinitionV2 definition, Dictionary<string, Binding> bindings)
    {
        _expected = definition; _bindings.Clear();
        foreach (var pair in bindings) _bindings.Add(pair.Key, pair.Value);
        _consumed = false; _generation++;
    }
    private void Clear() { _items.Clear(); _sources.Clear(); _bindings.Clear(); _expected = null; _effects = null; _directory = ""; _active = false; _generation++; }
    private void EnsureIdle() { ObjectDisposedException.ThrowIf(_disposed, this); if (_active) throw new InvalidOperationException("工作流正在执行。"); }
    private static int Select(Binding binding, int? index) => binding.Indices[index ?? 0];
    private ArtWorkflowArtifact SourceFor(Binding binding, int? index) => binding.OperationId is { } id ? _sources[id].Artifact : _items[Select(binding, index)].Source!;
    private void MarkFailure(Binding binding, int? index)
    {
        if (binding.Kind == "process") Uncertain(_items[Select(binding, index)]);
        else if (binding.Kind == "release") _sources[SourceFor(binding, index).OperationId].Status = "清理未确认，请重试或等待 TTL";
        else foreach (var i in binding.Indices) _items[i].Processing = "渲染未确认，请重新生成";
    }
    private static void Uncertain(Item item)
    {
        item.Started = false;
        item.Processing = "需核对：处理中断或结果未确认";
        if (!item.Uncertain.Contains(item.OutputPath, StringComparer.Ordinal)) item.Uncertain.Add(item.OutputPath);
    }
    private static bool SameDefinition(WorkflowDefinitionV2 a, WorkflowDefinitionV2 b) =>
        a.ContractRevision == b.ContractRevision && a.SchemaVersion == b.SchemaVersion && a.Steps.Count == b.Steps.Count &&
        a.Steps.Zip(b.Steps).All(pair => pair.First.Id == pair.Second.Id && pair.First.ActionId == pair.Second.ActionId &&
            pair.First.ForEach == pair.Second.ForEach && JsonElement.DeepEquals(pair.First.Arguments, pair.Second.Arguments));
    private sealed record Binding(string Kind, string ActionId, int[] Indices, Guid? OperationId = null);
    private sealed class OwnedSource(ArtWorkflowArtifact artifact)
    { public ArtWorkflowArtifact Artifact { get; } = artifact; public bool Released { get; set; } public string Status { get; set; } = "等待释放"; }
    private sealed class Item(ArtWorkflowRecipe recipe)
    {
        public ArtWorkflowRecipe Recipe { get; set; } = recipe;
        public string OutputPath { get; set; } = recipe.OutputPath;
        public ArtWorkflowArtifact? Source { get; set; }
        public bool Succeeded { get; set; }
        public bool Started { get; set; }
        public string Processing { get; set; } = "尚未处理";
        public List<string> Uncertain { get; } = [];
    }
}
