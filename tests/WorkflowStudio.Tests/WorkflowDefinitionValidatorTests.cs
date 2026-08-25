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

        var result = _validator.Validate(TestActions.ValidDefinition(catalog.Revision), catalog);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void 目录Revision变化后旧定义失效()
    {
        _secrets.Set("session-key", "x");
        var original = _catalog.Capture();
        var definition = TestActions.ValidDefinition(original.Revision);
        _gateway.Actions = [TestActions.Generate("变化"), TestActions.Format()];

        var result = _validator.Validate(definition, _catalog.Capture());

        Assert.Contains(result.Issues, item => item.Code == "catalog.stale");
    }

    [Fact]
    public void 缺失Secret和敏感字段明文都被拒绝()
    {
        var catalog = _catalog.Capture();
        var missing = _validator.Validate(TestActions.ValidDefinition(catalog.Revision), catalog);
        var clearText = new WorkflowDefinitionV1(1, catalog.Revision, "x",
        [
            new WorkflowStepDefinition("format", new(TestActions.FormatId),
                JsonSerializer.SerializeToElement(new { value = "x", secret = "plain-text" }))
        ]);
        var clearTextResult = _validator.Validate(clearText, catalog);

        Assert.Contains(missing.Issues, item => item.Code == "secret.missing");
        Assert.Contains(clearTextResult.Issues, item => item.Code == "secret.required");
    }

    [Fact]
    public void 重复非法步骤和未知Action被一次性报告()
    {
        var catalog = _catalog.Capture();
        var definition = new WorkflowDefinitionV1(2, catalog.Revision, "x",
        [
            new WorkflowStepDefinition("Bad_Id", new(TestActions.GenerateId),
                JsonSerializer.SerializeToElement(new { count = 1, prefix = "x" })),
            new WorkflowStepDefinition("Bad_Id", new("myavalonia.plugin.unknown.workflow.none"),
                JsonSerializer.SerializeToElement(new { }))
        ]);

        var result = _validator.Validate(definition, catalog);

        Assert.Contains(result.Issues, item => item.Code == "definition.schemaVersion");
        Assert.Contains(result.Issues, item => item.Code == "step.id");
        Assert.Contains(result.Issues, item => item.Code == "step.duplicate");
        Assert.Contains(result.Issues, item => item.Code == "action.unknown");
    }

    [Theory]
    [InlineData("${later.result.value}", "reference.step")]
    [InlineData("prefix-${generate.result.items}", "reference.syntax")]
    [InlineData("${item.value}", "reference.item")]
    public void 前向插值和跨作用域引用被拒绝(string value, string expectedCode)
    {
        _secrets.Set("session-key", "x");
        var catalog = _catalog.Capture();
        var definition = new WorkflowDefinitionV1(1, catalog.Revision, "x",
        [
            new WorkflowStepDefinition("format", new(TestActions.FormatId),
                JsonSerializer.SerializeToElement(new
                {
                    value,
                    secret = "${secret.session-key}"
                }))
        ]);

        var result = _validator.Validate(definition, catalog);

        Assert.Contains(result.Issues, item => item.Code == expectedCode);
    }

    [Fact]
    public void 非数组ForEach与不存在输出路径被拒绝()
    {
        _secrets.Set("session-key", "x");
        var catalog = _catalog.Capture();
        var definition = new WorkflowDefinitionV1(1, catalog.Revision, "x",
        [
            new WorkflowStepDefinition("generate", new(TestActions.GenerateId),
                JsonSerializer.SerializeToElement(new { count = 1, prefix = "x" })),
            new WorkflowStepDefinition("format", new(TestActions.FormatId),
                JsonSerializer.SerializeToElement(new { value = "${item.value}", secret = "${secret.session-key}" }),
                "${generate.result.missing}")
        ]);

        var result = _validator.Validate(definition, catalog);

        Assert.Contains(result.Issues, item => item.Code == "foreach.reference");
    }

    [Fact]
    public void Action输入Schema在引用解析前即可发现常量错误()
    {
        var catalog = _catalog.Capture();
        var definition = new WorkflowDefinitionV1(1, catalog.Revision, "x",
        [
            new WorkflowStepDefinition("generate", new(TestActions.GenerateId),
                JsonSerializer.SerializeToElement(new { count = 99, prefix = "", extra = true }))
        ]);

        var result = _validator.Validate(definition, catalog);

        Assert.Contains(result.Issues, item => item.Code == "schema.maximum");
        Assert.Contains(result.Issues, item => item.Code == "schema.minLength");
        Assert.Contains(result.Issues, item => item.Code == "schema.additionalProperties");
    }

    [Fact]
    public void 步骤与展开调用预算均会阻断执行()
    {
        _secrets.Set("session-key", "x");
        var catalog = _catalog.Capture();
        var steps = new List<WorkflowStepDefinition>
        {
            new("generate", new(TestActions.GenerateId),
                JsonSerializer.SerializeToElement(new { count = 1, prefix = "x" }))
        };
        for (var index = 0; index < 32; index++)
        {
            steps.Add(new WorkflowStepDefinition(
                "format-" + index,
                new(TestActions.FormatId),
                JsonSerializer.SerializeToElement(new { value = "${item.value}", secret = "${secret.session-key}" }),
                "${generate.result.items}"));
        }

        var result = _validator.Validate(new(1, catalog.Revision, "x", steps), catalog);

        Assert.Contains(result.Issues, item => item.Code == "budget.steps");
        Assert.Contains(result.Issues, item => item.Code == "budget.invocations");
    }

    [Fact]
    public void 风险摘要组合风险并使用最高确认频率()
    {
        var catalog = _catalog.Capture();
        var summary = new WorkflowRiskSummaryBuilder().Build(
            TestActions.ValidDefinition(catalog.Revision), catalog);

        Assert.Equal(WorkflowActionRiskFlags.HandlesSecret, summary.Risks);
        Assert.Equal(WorkflowActionConfirmationPolicy.OncePerRun, summary.HighestConfirmation);
        Assert.Equal(101, summary.MaximumInvocationCount);
    }

    public void Dispose() => _secrets.Dispose();
}
