using System.Security.Cryptography;
using System.Text.Json;

namespace WorkflowStudio.Workflows.ArtWorkflow;

/// <summary>仅保留 File Artifact v1 白名单字段；绝不把 Provider 完整 JSON 或任意附加值长期保存。</summary>
public sealed record ArtWorkflowArtifact(string ProducerPluginId, Guid OperationId, string Lifetime,
    string Path, long ByteLength, string Sha256)
{
    public const string Contract = "myavalonia.workflow.file-artifact";
    internal static string Root => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MyAvaloniaManagement", "WorkflowArtifacts");
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal string OperationDirectory => System.IO.Path.Combine(Root, ProducerPluginId, OperationId.ToString("D"));

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(new
    {
        contract = Contract,
        version = 1,
        producerPluginId = ProducerPluginId,
        producerOperationId = OperationId.ToString("D"),
        lifetime = Lifetime,
        path = Path,
        mediaType = "image/png",
        byteLength = ByteLength,
        sha256 = Sha256
    });

    internal static ArtWorkflowArtifact Parse(JsonElement value, string producer, string lifetime, string? expectedPath = null)
    {
        RequireFields(value, "contract", "version", "producerPluginId", "producerOperationId", "lifetime", "path", "mediaType", "byteLength", "sha256");
        var artifact = new ArtWorkflowArtifact(value.GetProperty("producerPluginId").GetString()!,
            value.GetProperty("producerOperationId").GetGuid(), value.GetProperty("lifetime").GetString()!,
            value.GetProperty("path").GetString()!, value.GetProperty("byteLength").GetInt64(), value.GetProperty("sha256").GetString()!);
        if (value.GetProperty("contract").GetString() != Contract || value.GetProperty("version").GetInt32() != 1 ||
            value.GetProperty("mediaType").GetString() != "image/png" || artifact.ProducerPluginId != producer ||
            artifact.Lifetime != lifetime || artifact.OperationId == Guid.Empty || artifact.ByteLength is < 8 or > 268435456 ||
            artifact.Sha256 is null || artifact.Sha256.Length != 64 || artifact.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F')) ||
            string.IsNullOrWhiteSpace(artifact.Path) || artifact.Path.Length > 32767 || !System.IO.Path.IsPathFullyQualified(artifact.Path))
            throw new InvalidDataException("返回的 File Artifact 不兼容。");
        var expected = expectedPath ?? System.IO.Path.Combine(artifact.OperationDirectory, "source.png");
        if (!string.Equals(System.IO.Path.GetFullPath(expected), System.IO.Path.GetFullPath(artifact.Path), PathComparison))
            throw new InvalidDataException("返回文件不属于预期操作或输出位置。");
        return artifact;
    }

    internal static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("返回值不是对象。");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject())
            if (!found.Add(p.Name) || !names.Contains(p.Name, StringComparer.Ordinal)) throw new InvalidDataException("返回值包含非预期字段。");
        if (found.Count != names.Length) throw new InvalidDataException("返回值缺少字段。");
    }

    internal static void ValidateImage(JsonElement image)
    {
        RequireFields(image, "width", "height");
        if (image.GetProperty("width").GetInt32() is < 1 or > 4096 || image.GetProperty("height").GetInt32() is < 1 or > 4096)
            throw new InvalidDataException("返回尺寸超出预算。");
    }
}

/// <summary>续跑前的只读源文件检查端口。它不授予调用权限，也不取代 ImageLab 的最终读取验证。</summary>
public interface IArtWorkflowSourceValidator
{
    Task ValidateAsync(ArtWorkflowArtifact artifact, CancellationToken token);
}

public sealed class ArtWorkflowSourceValidator : IArtWorkflowSourceValidator
{
    public async Task ValidateAsync(ArtWorkflowArtifact artifact, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = ArtWorkflowArtifact.Parse(artifact.ToJson(), ArtWorkflowDefinitionBuilder.FractalId, "run");
        RejectLinks(artifact.Path);
        var markerPath = System.IO.Path.Combine(artifact.OperationDirectory, ".owner.json");
        RejectLinks(markerPath);
        await using var markerStream = File.OpenRead(markerPath);
        if (markerStream.Length is < 1 or > 4096) throw new InvalidDataException("源文件所有权标记超限。");
        var markerBytes = new byte[4097];
        var read = 0;
        while (read < markerBytes.Length)
        {
            var next = await markerStream.ReadAsync(markerBytes.AsMemory(read), token).ConfigureAwait(false);
            if (next == 0) break;
            read += next;
        }
        if (read > 4096) throw new InvalidDataException("源文件所有权标记实际读取超限。");
        using var marker = JsonDocument.Parse(markerBytes.AsMemory(0, read));
        var owner = marker.RootElement;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in owner.EnumerateObject())
            if (!seen.Add(field.Name) || field.Name is not ("contract" or "version" or "producerPluginId" or "producerOperationId" or "createdAtUtc" or "invocationId" or "itemId"))
                throw new InvalidDataException("源文件所有权标记字段无效。");
        var created = owner.GetProperty("createdAtUtc").GetDateTimeOffset();
        if (owner.GetProperty("contract").GetString() != ArtWorkflowArtifact.Contract || owner.GetProperty("version").GetInt32() != 1 ||
            owner.GetProperty("producerPluginId").GetString() != artifact.ProducerPluginId ||
            owner.GetProperty("producerOperationId").GetGuid() != artifact.OperationId ||
            created < DateTimeOffset.UtcNow.AddHours(-24) || created > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new InvalidDataException("源文件所有权不匹配或已超过 24 小时恢复期限。");
        await using var stream = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != artifact.ByteLength) throw new InvalidDataException("源文件长度已变化。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, artifact.ByteLength - total + 1)), token).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > artifact.ByteLength) throw new InvalidDataException("源文件实际读取超限。");
            hash.AppendData(buffer, 0, count);
        }
        if (total != artifact.ByteLength || Convert.ToHexString(hash.GetHashAndReset()) != artifact.Sha256)
            throw new InvalidDataException("源文件摘要已变化。");
    }

    private static void RejectLinks(string path)
    {
        for (string? current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("源文件路径包含重解析点。");
    }
}
