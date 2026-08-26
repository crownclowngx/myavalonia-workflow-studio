using System.Text.Json;
using WorkflowStudio.Workflows;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class SecretAndSchemaTests
{
    [Fact]
    public void Secret可替换清空且名称严格受限()
    {
        using var store = new SessionSecretStore();
        store.Set("api-key", "first");
        store.Set("api-key", "second");

        Assert.True(store.TryGet("api-key", out var value));
        Assert.Equal("second", value);
        Assert.Equal(["api-key"], store.Names);
        Assert.Throws<ArgumentException>(() => store.Set("Bad_Name", "x"));
        store.Clear();
        Assert.False(store.Contains("api-key"));
        Assert.Empty(store.Names);
    }

    [Fact]
    public void Secret释放后拒绝继续读取或写入()
    {
        var store = new SessionSecretStore();
        store.Set("key", "value");
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => store.Set("key", "new"));
        Assert.Throws<ObjectDisposedException>(() => store.TryGet("key", out _));
    }

    [Fact]
    public void Schema校验覆盖类型枚举数组与数值边界()
    {
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"mode":{"type":"string","enum":["a","b"]},"items":{"type":"array","minItems":2,"maxItems":3,"items":{"type":"integer","minimum":1,"maximum":5}}},"required":["mode","items"],"additionalProperties":false}""");
        var value = JsonSerializer.SerializeToElement(new { mode = "c", items = new[] { 0, 6, 3, 4 }, extra = true });
        var issues = new List<WorkflowValidationIssue>();

        new WorkflowJsonSchemaValidator().Validate(value, schema.RootElement, "$", false, issues);

        Assert.Contains(issues, item => item.Code == "instance.enum");
        Assert.Contains(issues, item => item.Code == "instance.array.bounds");
        Assert.Contains(issues, item => item.Code == "instance.number.bounds");
        Assert.Contains(issues, item => item.Code == "instance.additional");
    }

    [Fact]
    public void Schema校验允许验证阶段引用占位但运行阶段拒绝()
    {
        using var schema = JsonDocument.Parse("""{"type":"integer"}""");
        var value = JsonSerializer.SerializeToElement("${previous.result.count}");
        var before = new List<WorkflowValidationIssue>();
        var after = new List<WorkflowValidationIssue>();
        var validator = new WorkflowJsonSchemaValidator();

        validator.Validate(value, schema.RootElement, "$", true, before);
        validator.Validate(value, schema.RootElement, "$", false, after);

        Assert.Empty(before);
        Assert.Contains(after, item => item.Code == "instance.type");
    }
}
