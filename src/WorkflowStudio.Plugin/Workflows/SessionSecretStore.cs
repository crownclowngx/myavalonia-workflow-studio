using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WorkflowStudio.Workflows;

public interface ISessionSecretStore
{
    void Set(string name, string value);
    bool Contains(string name);
    bool TryGet(string name, out string? value);
    IReadOnlyList<string> Names { get; }
    void Clear();
}
/// <summary>
/// 只在一个 Document Scope 内保存 Secret。内部使用字符数组，以便替换、关闭或释放时主动覆盖；
/// 调用 Gateway 时仍不可避免地会创建短生命周期字符串，但它不会进入定义、状态或诊断对象。
/// </summary>
public sealed partial class SessionSecretStore : ISessionSecretStore, IDisposable
{
    private readonly Dictionary<string, char[]> _values = new(StringComparer.Ordinal);
    private bool _disposed;

    public IReadOnlyList<string> Names => new ReadOnlyCollection<string>(
        _values.Keys.Order(StringComparer.Ordinal).ToArray());

    public void Set(string name, string value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!SecretNamePattern().IsMatch(name))
        {
            throw new ArgumentException("Secret 名称必须是小写字母开头的 kebab-case。", nameof(name));
        }

        if (_values.Remove(name, out var previous))
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(previous.AsSpan()));
        }
        _values.Add(name, value.ToCharArray());
    }

    public bool Contains(string name) => !_disposed && _values.ContainsKey(name);

    public bool TryGet(string name, out string? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_values.TryGetValue(name, out var characters))
        {
            value = new string(characters);
            return true;
        }
        value = null;
        return false;
    }

    public void Clear()
    {
        foreach (var characters in _values.Values)
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(characters.AsSpan()));
        }
        _values.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Clear();
        _disposed = true;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretNamePattern();
}
