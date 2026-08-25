using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Workflows;

namespace WorkflowStudio.Features.Main;

public enum WorkflowArgumentMode { Constant, Reference, Secret }

/// <summary>结构化编辑器中的一个顶层参数字段；复杂对象仍以严格 JSON 常量编辑，避免动态表单过度设计。</summary>
public sealed partial class WorkflowArgumentEditor : ObservableObject
{
    [ObservableProperty] private WorkflowArgumentMode _mode;
    [ObservableProperty] private string _value = "null";

    public required string Name { get; init; }
    public required string SchemaType { get; init; }
    public bool IsSensitive { get; init; }
    public IReadOnlyList<WorkflowArgumentMode> Modes { get; } =
        Enum.GetValues<WorkflowArgumentMode>();

    internal JsonNode? ToJsonNode()
    {
        if (Mode is WorkflowArgumentMode.Reference or WorkflowArgumentMode.Secret)
        {
            return JsonValue.Create(Value);
        }
        try
        {
            return JsonNode.Parse(Value);
        }
        catch (JsonException exception)
        {
            throw new WorkflowDefinitionFormatException($"参数 {Name} 的常量不是合法 JSON。", exception);
        }
    }
}

/// <summary>一个可排序步骤的编辑状态；它只负责收集输入，不承担验证或执行。</summary>
public sealed partial class WorkflowStepEditor : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string? _forEach;

    public required WorkflowActionDescriptor Action { get; init; }
    public ObservableCollection<WorkflowArgumentEditor> Arguments { get; } = [];
    public string ActionName => Action.DisplayName;
    public string ActionId => Action.Id.Value;

    internal WorkflowStepDefinition Build()
    {
        var arguments = new JsonObject();
        foreach (var argument in Arguments)
        {
            arguments.Add(argument.Name, argument.ToJsonNode());
        }
        return new WorkflowStepDefinition(
            Id,
            Action.Id,
            JsonSerializer.SerializeToElement(arguments),
            string.IsNullOrWhiteSpace(ForEach) ? null : ForEach);
    }
}

public sealed record WorkflowActionChoice(WorkflowActionDescriptor Descriptor)
{
    public string Display => $"{Descriptor.DisplayName}  ·  {Descriptor.Id.Value}";
}

public sealed record WorkflowValidationMessage(string Code, string Path, string Message)
{
    public string Display => $"[{Code}] {Path}：{Message}";
}

public sealed record WorkflowRunMessage(string Display);
