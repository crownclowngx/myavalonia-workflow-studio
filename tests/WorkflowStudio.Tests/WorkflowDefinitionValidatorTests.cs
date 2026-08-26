using System.Text.Json;
using MyAvaloniaManagement.PluginSdk;
using WorkflowStudio.Workflows;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class WorkflowDefinitionValidatorTests : IDisposable
{
    private readonly MutableGateway _gateway = new();
    private readonly WorkflowActionCatalogProjection _catalog;
    private readonly WorkflowDefinitionValidator _validator;
    private readonly SessionSecretStore _secrets;

    public WorkflowDefinitionValidatorTests()
    {
        var services = TestServiceFactory.Create(_gateway);
        _catalog = services.Catalog;
        _validator = services.Validator;
        _secrets = services.Secrets;
    }

    [Fact]
    public void 合法顺序ForEach与Secret引用通过验证()
    {
        _secrets.Set("session-key", "只在内存");
        var catalog = _catalog.Capture();

        var result = _validator.Validate(TestActions.ValidDefinition(catalog), catalog);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void 展示变化仅警告而契约变化阻止执行()
    {
        _secrets.Set("session-key", "x");
        var original = _catalog.Capture();
        var definition = TestActions.ValidDefinition(original);
        _gateway.Actions = [TestActions.Generate("已改名"), TestActions.Format()];

        var presentation = _validator.Validate(definition, _catalog.Capture());

        Assert.True(presentation.IsValid);
        Assert.Contains(presentation.Issues, item =>
            item.Code == "catalog.presentation-stale" &&
            item.Severity == WorkflowValidationSeverity.Warning);

        _gateway.Actions = [TestActions.Generate("已改名"), TestActions.Descriptor(
            TestActions.FormatId, "格式化", "处理测试项。",
            """{"type":"object","properties":{"value":{"type":"integer"},"secret":{"type":"string","minLength":1,"maxLength":64}},"required":["value","secret"],"additionalProperties":false}""",
            """{"type":"object","properties":{"formatted":{"type":"string","maxLength":64}},"required":["formatted"],"additionalProperties":false}""",
            WorkflowActionRiskFlags.HandlesSecret,
            WorkflowActionConfirmationPolicy.OncePerRun,
            ["/secret"])];
        var contract = _validator.Validate(definition, _catalog.Capture());
        Assert.False(contract.IsValid);
        Assert.Contains(contract.Issues, item => item.Code == "catalog.contract-stale");
    }

    [Fact]
    public void 缺失Secret和敏感字段明文都被拒绝()
    {
        var catalog = _catalog.Capture();
        var missing = _validator.Validate(TestActions.ValidDefinition(catalog), catalog);
        var clearText = Definition(catalog,
        [
            new("format", new(TestActions.FormatId),
                JsonSerializer.SerializeToElement(new { value = "x", secret = "plain-text" }))
        ]);

        Assert.Contains(missing.Issues, item => item.Code == "secret.missing");
        Assert.Contains(_validator.Validate(clearText, catalog).Issues,
            item => item.Code == "secret.required");
    }

    [Fact]
    public void 当前Secret真实值不满足目标长度时被拒绝()
    {
        _secrets.Set("session-key", string.Empty);
        var catalog = _catalog.Capture();

        var result = _validator.Validate(TestActions.ValidDefinition(catalog), catalog);

        Assert.Contains(result.Issues, item => item.Code == "reference.secret-type");
    }

    [Fact]
    public void 非法版本重复步骤与未知Action被一次性报告()
    {
        var catalog = _catalog.Capture();
        var definition = new WorkflowDefinitionV2(1, catalog.ContractRevision,
            catalog.PresentationRevision, "x",
        [
            new("Bad_Id", new(TestActions.GenerateId),
                JsonSerializer.SerializeToElement(new { count = 1, prefix = "x" })),
            new("Bad_Id", new("myavalonia.plugin.unknown.workflow.none"),
                JsonSerializer.SerializeToElement(new { }))
        ]);

        var result = _validator.Validate(definition, catalog);

        Assert.Contains(result.Issues, item => item.Code == "definition.schemaVersion");
        Assert.Contains(result.Issues, item => item.Code == "step.id");
        Assert.Contains(result.Issues, item => item.Code == "step.duplicate");
        Assert.Contains(result.Issues, item => item.Code == "action.unknown");
    }

    [Fact]
    public void 空定义和超长摘要被预算规则拒绝()
    {
        var catalog = _catalog.Capture();
        var definition = new WorkflowDefinitionV2(2, catalog.ContractRevision,
            catalog.PresentationRevision, new string('x', 64 * 1024 + 1), []);

        var result = _validator.Validate(definition, catalog);

        Assert.Contains(result.Issues, item => item.Code == "definition.empty");
        Assert.Contains(result.Issues, item => item.Code == "budget.string");
    }

    [Theory]
    [InlineData("${later.result.value}", "reference.step")]
    [InlineData("prefix-${generate.result.items}", "reference.syntax")]
    [InlineData("${item.value}", "reference.item")]
    public void 前向插值和跨作用域引用被拒绝(string value, string expectedCode)
    {
        _secrets.Set("session-key", "x");
        var catalog = _catalog.Capture();
        var definition = Definition(catalog,
        [
            new("format", new(TestActions.FormatId), JsonSerializer.SerializeToElement(new
            {
                value,
                secret = "${secret.session-key}"
            }))
        ]);

        Assert.Contains(_validator.Validate(definition, catalog).Issues,
            item => item.Code == expectedCode);
    }

    [Fact]
    public void Optional输出与非法数组索引在运行前拒绝()
    {
        _secrets.Set("session-key", "x");
        var optional = TestActions.Descriptor(
            TestActions.GenerateId, "生成", "可选输出",
            """{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"],"additionalProperties":false}""",
            """{"type":"object","properties":{"value":{"type":"string","minLength":1,"maxLength":32},"items":{"type":"array","minItems":1,"maxItems":2,"items":{"type":"string","minLength":1,"maxLength":32}}},"required":["items"],"additionalProperties":false}""",
            WorkflowActionRiskFlags.None, WorkflowActionConfirmationPolicy.Never);
        _gateway.Actions = [optional, TestActions.Format()];
        var catalog = _catalog.Capture();
        var steps = new List<WorkflowStepDefinition>
        {
            new("generate", new(TestActions.GenerateId), JsonSerializer.SerializeToElement(new { count = 1 })),
            new("format", new(TestActions.FormatId), JsonSerializer.SerializeToElement(new
            {
                value = "${generate.result.value}", secret = "${secret.session-key}"
            }))
        };

        var optionalResult = _validator.Validate(Definition(catalog, steps), catalog);
        Assert.Contains(optionalResult.Issues, item => item.Code == "reference.optional");

        steps[1] = new("format", new(TestActions.FormatId), JsonSerializer.SerializeToElement(new
        {
            value = "${generate.result.items.bad}",
            secret = "${secret.session-key}"
        }));
        var indexResult = _validator.Validate(Definition(catalog, steps), catalog);
        Assert.Contains(indexResult.Issues, item => item.Code == "reference.array-index");
    }

    [Fact]
    public void ForEach非法来源非数组来源和Item缺失路径分别拒绝()
    {
        _secrets.Set("session-key", "x");
        var catalog = _catalog.Capture();
        var arguments = JsonSerializer.SerializeToElement(new
        {
            value = "${item.missing}",
            secret = "${secret.session-key}"
        });

        var invalidKind = Definition(catalog,
        [
            new("format", new(TestActions.FormatId), arguments, "${item.value}")
        ]);
        Assert.Contains(_validator.Validate(invalidKind, catalog).Issues,
            item => item.Code == "foreach.reference");

        var missingPrior = Definition(catalog,
        [
            new("format", new(TestActions.FormatId), arguments, "${missing.result.items}")
        ]);
        Assert.Contains(_validator.Validate(missingPrior, catalog).Issues,
            item => item.Code == "reference.step");

        var scalarSource = TestActions.Descriptor(
            TestActions.GenerateId, "源", "标量来源",
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""",
            """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""",
            WorkflowActionRiskFlags.None, WorkflowActionConfirmationPolicy.Never);
        _gateway.Actions = [scalarSource, TestActions.Format()];
        catalog = _catalog.Capture();
        var notArray = Definition(catalog,
        [
            new("source", new(TestActions.GenerateId), JsonSerializer.SerializeToElement(new { })),
            new("format", new(TestActions.FormatId), arguments, "${source.result.value}")
        ]);
        Assert.Contains(_validator.Validate(notArray, catalog).Issues,
            item => item.Code == "foreach.array");

        _gateway.Actions = [TestActions.Generate(), TestActions.Format()];
        catalog = _catalog.Capture();
        var missingItem = Definition(catalog,
        [
            new("generate", new(TestActions.GenerateId),
                JsonSerializer.SerializeToElement(new { count = 1, prefix = "x" })),
            new("format", new(TestActions.FormatId), arguments, "${generate.result.items}")
        ]);
        Assert.Contains(_validator.Validate(missingItem, catalog).Issues,
            item => item.Code == "reference.path");
    }

    [Fact]
    public void ForEach来源超过单循环预算和总调用预算时拒绝()
    {
        var source = TestActions.Descriptor(
            TestActions.GenerateId, "源", "大数组",
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""",
            """{"type":"object","properties":{"items":{"type":"array","minItems":1,"maxItems":101,"items":{"type":"string","minLength":1,"maxLength":8}}},"required":["items"],"additionalProperties":false}""",
            WorkflowActionRiskFlags.None, WorkflowActionConfirmationPolicy.Never);
        var sink = TestActions.Descriptor(
            TestActions.FormatId, "目标", "空参数",
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""",
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""",
            WorkflowActionRiskFlags.None, WorkflowActionConfirmationPolicy.Never);
        _gateway.Actions = [source, sink];
        var catalog = _catalog.Capture();
        var steps = new List<WorkflowStepDefinition>
        {
            new("source", new(TestActions.GenerateId), JsonSerializer.SerializeToElement(new { }))
        };
        for (var index = 0; index < 3; index++)
        {
            steps.Add(new($"sink-{index}", new(TestActions.FormatId),
                JsonSerializer.SerializeToElement(new { }), "${source.result.items}"));
        }

        var result = _validator.Validate(Definition(catalog, steps), catalog);

        Assert.Contains(result.Issues, item => item.Code == "budget.foreach");
        Assert.Contains(result.Issues, item => item.Code == "budget.invocations");
    }

    [Fact]
    public void 数组参数中的引用递归执行类型检查()
    {
        var source = TestActions.Descriptor(
            TestActions.GenerateId, "源", "字符串",
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""",
            """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""",
            WorkflowActionRiskFlags.None, WorkflowActionConfirmationPolicy.Never);
        var sink = TestActions.Descriptor(
            TestActions.FormatId, "目标", "数组",
            """{"type":"object","properties":{"values":{"type":"array","maxItems":2,"items":{"type":"integer"}}},"required":["values"],"additionalProperties":false}""",
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""",
            WorkflowActionRiskFlags.None, WorkflowActionConfirmationPolicy.Never);
        _gateway.Actions = [source, sink];
        var catalog = _catalog.Capture();
        var definition = Definition(catalog,
        [
            new("source", new(TestActions.GenerateId), JsonSerializer.SerializeToElement(new { })),
            new("sink", new(TestActions.FormatId), JsonSerializer.SerializeToElement(new
            {
                values = new[] { "${source.result.value}" }
            }))
        ]);

        Assert.Contains(_validator.Validate(definition, catalog).Issues,
            item => item.Code == "reference.type" && item.Path.EndsWith("[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void 不兼容类型与范围被拒绝而Integer可赋给Number()
    {
        _secrets.Set("session-key", "x");
        var source = TestActions.Descriptor(
            TestActions.GenerateId, "源", "源",
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""",
            """{"type":"object","properties":{"text":{"type":"string"},"count":{"type":"integer","minimum":1,"maximum":3}},"required":["text","count"],"additionalProperties":false}""",
            WorkflowActionRiskFlags.None, WorkflowActionConfirmationPolicy.Never);
        var target = TestActions.Descriptor(
            TestActions.FormatId, "目标", "目标",
            """{"type":"object","properties":{"number":{"type":"number","minimum":0,"maximum":10},"integer":{"type":"integer"}},"required":["number","integer"],"additionalProperties":false}""",
            """{"type":"object","properties":{},"required":[],"additionalProperties":false}""",
            WorkflowActionRiskFlags.None, WorkflowActionConfirmationPolicy.Never);
        _gateway.Actions = [source, target];
        var catalog = _catalog.Capture();
        var definition = Definition(catalog,
        [
            new("source", new(TestActions.GenerateId), JsonSerializer.SerializeToElement(new { })),
            new("target", new(TestActions.FormatId), JsonSerializer.SerializeToElement(new
            {
                number = "${source.result.count}",
                integer = "${source.result.text}"
            }))
        ]);

        var result = _validator.Validate(definition, catalog);

        Assert.Single(result.Issues, item => item.Code == "reference.type");
        Assert.DoesNotContain(result.Issues,
            item => item.Path.EndsWith(".number", StringComparison.Ordinal));
    }

    [Fact]
    public void 常量错误与步骤预算被结构化报告()
    {
        var catalog = _catalog.Capture();
        var invalid = Definition(catalog,
        [
            new("generate", new(TestActions.GenerateId),
                JsonSerializer.SerializeToElement(new { count = 99, prefix = "", extra = true }))
        ]);
        var result = _validator.Validate(invalid, catalog);
        Assert.Contains(result.Issues, item => item.Code == "instance.number.bounds");
        Assert.Contains(result.Issues, item => item.Code == "instance.string.bounds");
        Assert.Contains(result.Issues, item => item.Code == "instance.additional");

        _secrets.Set("session-key", "x");
        var steps = new List<WorkflowStepDefinition>
        {
            new("generate", new(TestActions.GenerateId),
                JsonSerializer.SerializeToElement(new { count = 1, prefix = "x" }))
        };
        for (var index = 0; index < 32; index++)
        {
            steps.Add(new("format-" + index, new(TestActions.FormatId),
                JsonSerializer.SerializeToElement(new
                {
                    value = "${item.value}",
                    secret = "${secret.session-key}"
                }), "${generate.result.items}"));
        }
        var budget = _validator.Validate(Definition(catalog, steps), catalog);
        Assert.Contains(budget.Issues, item => item.Code == "budget.steps");
    }

    [Fact]
    public void 风险摘要组合风险并使用最高确认频率()
    {
        var catalog = _catalog.Capture();
        var summary = new WorkflowRiskSummaryBuilder().Build(TestActions.ValidDefinition(catalog), catalog);

        Assert.Equal(WorkflowActionRiskFlags.HandlesSecret, summary.Risks);
        Assert.Equal(WorkflowActionConfirmationPolicy.OncePerRun, summary.HighestConfirmation);
        Assert.Equal(101, summary.MaximumInvocationCount);
    }

    private static WorkflowDefinitionV2 Definition(
        WorkflowActionCatalogSnapshot catalog,
        IReadOnlyList<WorkflowStepDefinition> steps) => new(
        2, catalog.ContractRevision, catalog.PresentationRevision, "x", steps);

    public void Dispose() => _secrets.Dispose();
}
