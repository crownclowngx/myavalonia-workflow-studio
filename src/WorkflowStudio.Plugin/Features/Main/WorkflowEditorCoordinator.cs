using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Workflows;

namespace WorkflowStudio.Features.Main;

/// <summary>表示导入后可一次性映射到 MainDocument 的编辑器快照。</summary>
public sealed record WorkflowEditorSnapshot(
    string Summary,
    IReadOnlyList<WorkflowStepEditor> Steps);

/// <summary>把确定性验证结果与风险确认摘要绑定到同一目录快照。</summary>
public sealed record WorkflowEditorValidation(
    WorkflowValidationResult Validation,
    WorkflowRiskSummary Risk);

/// <summary>封装目录、步骤构造、v2 导入导出与运行前验证用例。</summary>
public interface IWorkflowEditorCoordinator
{
    WorkflowActionCatalogSnapshot CaptureCatalog();
    WorkflowStepEditor CreateStep(WorkflowActionDescriptor action, string id, string? forEach);
    WorkflowDefinitionV2 BuildDefinition(
        WorkflowActionCatalogSnapshot catalog,
        string summary,
        IReadOnlyList<WorkflowStepEditor> steps);
    WorkflowEditorSnapshot Import(string json, WorkflowActionCatalogSnapshot catalog);
    string Export(WorkflowDefinitionV2 definition);
    WorkflowEditorValidation Validate(
        WorkflowDefinitionV2 definition,
        WorkflowActionCatalogSnapshot catalog);
}

/// <summary>集中承载编辑、导入导出、定义构造和确定性验证用例。</summary>
/// <remarks>
/// 协调器返回普通快照，不拥有 ObservableCollection 或命令状态；MainDocument 因而只需负责
/// UI 映射，而 Codec、目录与验证服务仍可独立替换和测试。
/// </remarks>
public sealed class WorkflowEditorCoordinator(
    IWorkflowActionCatalogProjection catalogProjection,
    IWorkflowDefinitionCodec codec,
    IWorkflowDefinitionValidator validator,
    IWorkflowRiskSummaryBuilder riskBuilder) : IWorkflowEditorCoordinator
{
    public WorkflowActionCatalogSnapshot CaptureCatalog() => catalogProjection.Capture();

    public WorkflowStepEditor CreateStep(
        WorkflowActionDescriptor action,
        string id,
        string? forEach)
    {
        var editor = new WorkflowStepEditor { Action = action, Id = id, ForEach = forEach };
        if (!action.InputSchema.TryGetProperty("properties", out var properties))
        {
            return editor;
        }
        foreach (var property in properties.EnumerateObject())
        {
            var pointer = "/" + property.Name.Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal);
            var sensitive = action.SensitiveInputPointers.Contains(pointer, StringComparer.Ordinal);
            var type = WorkflowJsonSchemaValidator.SchemaType(property.Value) ?? "json";
            editor.Arguments.Add(new WorkflowArgumentEditor
            {
                Name = property.Name,
                SchemaType = type,
                IsSensitive = sensitive,
                Mode = sensitive ? WorkflowArgumentMode.Secret : WorkflowArgumentMode.Constant,
                Value = sensitive ? "${secret.session-key}" : DefaultConstant(type),
            });
        }
        return editor;
    }

    public WorkflowDefinitionV2 BuildDefinition(
        WorkflowActionCatalogSnapshot catalog,
        string summary,
        IReadOnlyList<WorkflowStepEditor> steps) => new(
        2,
        catalog.ContractRevision,
        catalog.PresentationRevision,
        summary,
        steps.Select(item => item.Build()).ToArray());

    public WorkflowEditorSnapshot Import(string json, WorkflowActionCatalogSnapshot catalog)
    {
        var definition = codec.Parse(json);
        var imported = new List<WorkflowStepEditor>();
        foreach (var step in definition.Steps)
        {
            if (!catalog.TryGet(step.ActionId, out var action))
            {
                throw new WorkflowDefinitionFormatException(
                    $"导入定义包含未知 Action：{step.ActionId.Value}。");
            }
            var editor = CreateStep(action!, step.Id, step.ForEach);
            foreach (var argument in editor.Arguments)
            {
                if (!step.Arguments.TryGetProperty(argument.Name, out var value))
                {
                    continue;
                }
                var text = value.ValueKind == JsonValueKind.String
                    ? value.GetString()!
                    : value.GetRawText();
                if (WorkflowReferenceToken.TryParse(text, out var token))
                {
                    argument.Mode = token!.Kind == WorkflowReferenceKind.Secret
                        ? WorkflowArgumentMode.Secret
                        : WorkflowArgumentMode.Reference;
                    argument.Value = text;
                }
                else
                {
                    argument.Mode = WorkflowArgumentMode.Constant;
                    argument.Value = value.GetRawText();
                }
            }
            imported.Add(editor);
        }
        return new(definition.Summary, imported);
    }

    public string Export(WorkflowDefinitionV2 definition) => codec.Serialize(definition);

    public WorkflowEditorValidation Validate(
        WorkflowDefinitionV2 definition,
        WorkflowActionCatalogSnapshot catalog) =>
        new(validator.Validate(definition, catalog), riskBuilder.Build(definition, catalog));

    private static string DefaultConstant(string type) => type switch
    {
        "string" => "\"\"",
        "integer" or "number" => "0",
        "boolean" => "false",
        "array" => "[]",
        "object" => "{}",
        _ => "null",
    };
}
