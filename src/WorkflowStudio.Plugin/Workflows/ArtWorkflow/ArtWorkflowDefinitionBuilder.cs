using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.Workflow;

namespace WorkflowStudio.Workflows.ArtWorkflow;

/// <summary>Studio 自有的表单快照，只有 JSON 字段越过 Gateway，不引用两侧插件 DTO。</summary>
public sealed record ArtWorkflowEffects(bool BlurEnabled = true, double BlurSigma = 1.5,
    bool BloomEnabled = true, double BloomThreshold = .72, double BloomSigma = 5, double BloomStrength = .8,
    bool GrainEnabled = true, double GrainAmount = 3, long GrainSeed = 0)
{
    public void Validate()
    {
        if (!Range(BlurSigma, 0, 10) || !Range(BloomThreshold, 0, 1) || !Range(BloomSigma, .1, 10) ||
            !Range(BloomStrength, 0, 4) || !Range(GrainAmount, 0, 100))
            throw new InvalidDataException("效果参数超出 ImageLab 支持范围。");
    }
    private static bool Range(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}

public sealed record ArtWorkflowRecipe(string ItemId, string RecipePath, string OutputPath);

/// <summary>一次生成的不可变输入与定义；后续表单变化不能偷偷改变正在恢复的批次。</summary>
public sealed record ArtWorkflowPlan(WorkflowDefinitionV2 Definition, IReadOnlyList<ArtWorkflowRecipe> Items,
    ArtWorkflowEffects Effects, string OutputDirectory);

/// <summary>
/// 构造产品示例与恢复定义。使用现有 v2/ForEach，仅引用必然存在的字段；不推断批结果数组长度，
/// 不拼接引用字符串，也不构造假的目录 revision。所有定义最终由同一个生产验证器检验。
/// </summary>
public sealed class ArtWorkflowDefinitionBuilder(IWorkflowDefinitionValidator validator)
{
    public const string FractalId = "myavalonia.plugin.fractal.art";
    public const string ImageLabId = "myavalonia.plugin.image.lab";
    public const string Render = FractalId + ".workflow.render-artwork-file";
    public const string Batch = FractalId + ".workflow.export-artwork-batch";
    public const string Release = FractalId + ".workflow.release-artifact";
    public const string Apply = ImageLabId + ".workflow.apply-art-effects-file";
    public const string ApplyDirectory = ImageLabId + ".workflow.apply-art-effects-file-to-directory";

    public ArtWorkflowPlan Create(WorkflowActionCatalogSnapshot catalog, IReadOnlyList<string> paths,
        string outputDirectory, ArtWorkflowEffects effects)
    {
        if (paths.Count is < 1 or > 16) throw new InvalidDataException("请选择 1–16 个已保存配方。");
        if (paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 32767 || !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("配方必须使用有效绝对路径。");
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory) || outputDirectory.Length > 32700)
            throw new InvalidDataException("请选择有效的绝对输出目录。");
        effects.Validate();
        var batchId = Guid.NewGuid().ToString("N");
        var items = paths.Select((path, index) =>
        {
            var id = $"fractal-{batchId}-{index + 1:D2}";
            return new ArtWorkflowRecipe(id, path, Path.Combine(outputDirectory, id + ".png"));
        }).ToArray();
        var steps = new List<WorkflowStepDefinition>();
        if (items.Length == 1)
        {
            steps.Add(Step("render", Render, new { recipePath = items[0].RecipePath }));
            steps.Add(Step("process", Apply, ApplyArguments("${render.result.artifact}", effects, items[0].OutputPath)));
            steps.Add(Step("release", Release, new { artifact = "${render.result.artifact}" }));
        }
        else
        {
            steps.Add(Step("render-batch", Batch,
                new { items = items.Select(item => new { itemId = item.ItemId, recipePath = item.RecipePath }) }));
            steps.Add(Step("process", ApplyDirectory,
                DirectoryArguments("${item.artifact}", effects, outputDirectory, "${item.itemId}"), "${render-batch.result.results}"));
            steps.Add(Step("release", Release, new { artifact = "${item.artifact}" }, "${render-batch.result.results}"));
        }
        return new(Validate(catalog, steps), Array.AsReadOnly(items), effects, outputDirectory);
    }

    public WorkflowDefinitionV2 Validate(WorkflowActionCatalogSnapshot catalog, IReadOnlyList<WorkflowStepDefinition> steps)
    {
        foreach (var step in steps)
        {
            if (!catalog.TryGet(step.ActionId, out var action))
                throw new InvalidDataException($"所需 Action 不可用：{step.ActionId.Value}。请安装兼容插件并刷新目录。");
            var deletion = step.ActionId.Value == Release;
            var risks = deletion ? WorkflowActionRiskFlags.DeletesLocalFiles :
                WorkflowActionRiskFlags.ReadsLocalFiles | WorkflowActionRiskFlags.WritesLocalFiles | WorkflowActionRiskFlags.LongRunning;
            if (action!.Risks != risks || action.ConfirmationPolicy != (deletion ?
                    WorkflowActionConfirmationPolicy.EveryInvocation : WorkflowActionConfirmationPolicy.OncePerRun) ||
                action.SensitiveInputPointers.Count != 0)
                throw new InvalidDataException("所需 Action 的风险或敏感字段声明不兼容，不能生成可恢复示例。");
            ValidateOutputContract(action);
        }
        var definition = new WorkflowDefinitionV2(2, catalog.ContractRevision, catalog.PresentationRevision,
            "Fractal → ImageLab：文件式后处理（非实时效果）", steps);
        var validation = validator.Validate(definition, catalog);
        if (!validation.IsValid) throw new WorkflowValidationException(validation);
        return definition;
    }

    /// <summary>
    /// 普通验证器只验证步骤之间的引用；ImageLab 的最终输出没有下游读取者，不能据此证明可恢复。
    /// 因此示例额外验证将要保留的输出协议，避免已经运行并释放源后才发现 Artifact 版本不兼容。
    /// </summary>
    private static void ValidateOutputContract(WorkflowActionDescriptor action)
    {
        if (!new WorkflowSchemaValidator().ValidateDescriptor(action).IsValid)
            throw new InvalidDataException("所需 Action 的 Schema 不符合当前 Workflow 协议。");
        if (action.Id.Value == Release)
        {
            _ = Required(action.OutputSchema, ["released"], "boolean");
            return;
        }
        var output = action.OutputSchema;
        if (action.Id.Value == Batch)
            output = Required(output, ["results"], "array").GetProperty("items");
        var artifact = Required(output, ["artifact"], "object");
        RequireEnum(Required(artifact, ["version"], "integer"), JsonSerializer.SerializeToElement(1));
        RequireEnum(Required(artifact, ["contract"], "string"), JsonSerializer.SerializeToElement(ArtWorkflowArtifact.Contract));
        var fractal = action.Id.Value is Render or Batch;
        RequireEnum(Required(artifact, ["producerPluginId"], "string"), JsonSerializer.SerializeToElement(fractal ? FractalId : ImageLabId));
        RequireEnum(Required(artifact, ["lifetime"], "string"), JsonSerializer.SerializeToElement(fractal ? "run" : "persistent"));
        RequireEnum(Required(artifact, ["mediaType"], "string"), JsonSerializer.SerializeToElement("image/png"));
        foreach (var (name, length) in new[] { ("producerOperationId", 36), ("sha256", 64) })
        {
            var field = Required(artifact, [name], "string");
            if (!field.TryGetProperty("minLength", out var min) || !field.TryGetProperty("maxLength", out var max) ||
                min.GetInt32() != length || max.GetInt32() != length)
                throw new InvalidDataException("Artifact 身份或摘要长度契约不兼容。");
        }
        _ = Required(artifact, ["path"], "string");
        _ = Required(artifact, ["byteLength"], "integer");
        _ = Required(output, ["image", "width"], "integer");
        _ = Required(output, ["image", "height"], "integer");
    }

    private static JsonElement Required(JsonElement schema, string[] path, string type)
    {
        var result = WorkflowReferencePath.ResolveGuaranteedSchemaPath(schema, path);
        if (!result.Succeeded || result.Value!.Value.GetProperty("type").GetString() != type)
            throw new InvalidDataException("所需 Action 缺少可恢复示例要求的输出字段或类型。");
        return result.Value.Value;
    }

    private static void RequireEnum(JsonElement schema, JsonElement value)
    {
        if (!schema.TryGetProperty("enum", out var values) || values.GetArrayLength() != 1 || !JsonElement.DeepEquals(values[0], value))
            throw new InvalidDataException("所需 Action 的 File Artifact 版本、生产者或生命周期不兼容。");
    }

    internal static WorkflowStepDefinition Step(string id, string action, object arguments, string? forEach = null) =>
        new(id, new WorkflowActionId(action), JsonSerializer.SerializeToElement(arguments), forEach);

    internal static object ApplyArguments(object source, ArtWorkflowEffects e, string path) => new
    {
        source,
        blur = new { enabled = e.BlurEnabled, sigma = e.BlurSigma },
        bloom = new { enabled = e.BloomEnabled, threshold = e.BloomThreshold, sigma = e.BloomSigma, strength = e.BloomStrength },
        grain = new { enabled = e.GrainEnabled, amount = e.GrainAmount, seed = e.GrainSeed },
        outputPath = path
    };

    private static object DirectoryArguments(object source, ArtWorkflowEffects e, string directory, string stem) => new
    {
        source,
        blur = new { enabled = e.BlurEnabled, sigma = e.BlurSigma },
        bloom = new { enabled = e.BloomEnabled, threshold = e.BloomThreshold, sigma = e.BloomSigma, strength = e.BloomStrength },
        grain = new { enabled = e.GrainEnabled, amount = e.GrainAmount, seed = e.GrainSeed },
        outputDirectory = directory,
        fileStem = stem
    };
}
