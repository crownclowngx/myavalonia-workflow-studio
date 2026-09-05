using Avalonia.Platform.Storage;

namespace WorkflowStudio.Features.Main;

/// <summary>View 附着时提供当前窗口的 StorageProvider；分离时清除引用，防止 Scoped 服务滞留旧窗口。</summary>
public sealed class ArtWorkflowFilePicker : IArtWorkflowFilePicker
{
    private IStorageProvider? _storage;
    internal void Attach(IStorageProvider? storage) => _storage = storage;
    public async Task<IReadOnlyList<string>> PickRecipesAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var files = await RequireStorage().OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Fractal Workflow 配方",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Fractal Workflow 配方") { Patterns = ["*.fractal-workflow.json", "*.json"] }]
        });
        try
        {
            token.ThrowIfCancellationRequested();
            return files.Select(file => file.TryGetLocalPath() ?? throw new InvalidDataException("配方必须是本地文件。")).ToArray();
        }
        finally { foreach (var file in files) file.Dispose(); }
    }
    public async Task<string?> PickDirectoryAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var folders = await RequireStorage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择 PNG 输出目录" });
        try { token.ThrowIfCancellationRequested(); return folders.FirstOrDefault()?.TryGetLocalPath(); }
        finally { foreach (var folder in folders) folder.Dispose(); }
    }
    private IStorageProvider RequireStorage() => _storage ?? throw new InvalidOperationException("当前视图尚未附着到支持文件选择的窗口。");
}
