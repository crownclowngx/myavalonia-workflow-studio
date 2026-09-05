using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkflowStudio.Workflows;
using WorkflowStudio.Workflows.ArtWorkflow;

namespace WorkflowStudio.Features.Main;

/// <summary>只选择本地配方与既有目录；不读取作品正文，不在 UI 线程执行文件处理。</summary>
public interface IArtWorkflowFilePicker
{
    Task<IReadOnlyList<string>> PickRecipesAsync(CancellationToken token);
    Task<string?> PickDirectoryAsync(CancellationToken token);
}

/// <summary>
/// 示例面板只适配表单与命令。构造定义、协议校验和恢复台账分别由应用服务负责。
/// 准备操作只替换编辑器，用户仍通过主界面的执行命令触发 Host 授权。
/// </summary>
public sealed partial class ArtWorkflowPanel : ObservableObject
{
    private readonly ArtWorkflowDefinitionBuilder _builder;
    private readonly ArtWorkflowRecoverySession _recovery;
    private readonly IWorkflowActionCatalogProjection _catalog;
    private readonly IWorkflowRunSession _runs;
    private readonly MyAvaloniaManagement.PluginSdk.IDocumentLifetime _lifetime;
    private CancellationTokenSource? _operationCancellation;
    private bool _closed;
    private CancellationToken OperationToken => _operationCancellation?.Token ?? _lifetime.ClosingToken;
    public IArtWorkflowFilePicker Files { get; }
    public event Action<WorkflowDefinitionV2, WorkflowActionCatalogSnapshot>? DefinitionPrepared;
    public event Action<bool>? BusyChanged;
    public ObservableCollection<string> Recipes { get; } = [];
    public ObservableCollection<ArtWorkflowItemStatus> Results { get; } = [];
    [ObservableProperty] private int _selectedIndex = -1;
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "选择 1–16 个配方与输出目录；这里只处理导出文件，不影响 Fractal 实时效果。";
    [ObservableProperty] private bool _blurEnabled = true;
    [ObservableProperty] private double _blurSigma = 1.5;
    [ObservableProperty] private bool _bloomEnabled = true;
    [ObservableProperty] private double _bloomThreshold = .72;
    [ObservableProperty] private double _bloomSigma = 5;
    [ObservableProperty] private double _bloomStrength = .8;
    [ObservableProperty] private bool _grainEnabled = true;
    [ObservableProperty] private double _grainAmount = 3;
    [ObservableProperty] private long _grainSeed;

    public ArtWorkflowPanel(ArtWorkflowDefinitionBuilder builder, ArtWorkflowRecoverySession recovery,
        IWorkflowActionCatalogProjection catalog, IWorkflowRunSession runs, IArtWorkflowFilePicker files,
        MyAvaloniaManagement.PluginSdk.IDocumentLifetime lifetime)
    {
        _builder = builder; _recovery = recovery; _catalog = catalog; _runs = runs; Files = files; _lifetime = lifetime;
        AddRecipesCommand = new AsyncRelayCommand(() => GuardAsync(async () =>
        {
            var selected = await Files.PickRecipesAsync(OperationToken);
            OperationToken.ThrowIfCancellationRequested();
            if (Recipes.Count + selected.Count > 16) throw new InvalidDataException("最多选择 16 个配方；本次选择未加入。");
            foreach (var path in selected) Recipes.Add(path);
        }), Idle);
        ChooseDirectoryCommand = new AsyncRelayCommand(() => GuardAsync(async () =>
        {
            var directory = await Files.PickDirectoryAsync(OperationToken);
            OperationToken.ThrowIfCancellationRequested();
            if (directory is not null) OutputDirectory = directory;
        }), Idle);
        RemoveRecipeCommand = new RelayCommand(() => { if (SelectedIndex >= 0 && SelectedIndex < Recipes.Count) Recipes.RemoveAt(SelectedIndex); }, Idle);
        MoveUpCommand = new RelayCommand(() => Move(-1), Idle);
        MoveDownCommand = new RelayCommand(() => Move(1), Idle);
        CreateCommand = new AsyncRelayCommand(() => GuardAsync(() =>
        {
            var catalogSnapshot = _catalog.Capture();
            var plan = _builder.Create(catalogSnapshot, Recipes.ToArray(), OutputDirectory, Effects());
            _recovery.Attach(plan);
            DefinitionPrepared?.Invoke(plan.Definition, catalogSnapshot);
            Status = "完整定义已生成并验证，请点击主工具栏“执行”；文件读取、写入和释放由 Host 确认。";
            return Task.CompletedTask;
        }), Idle);
        ResumeCommand = new AsyncRelayCommand(() => GuardAsync(async () =>
        {
            var snapshot = _catalog.Capture();
            var definition = await _recovery.PrepareResumeAsync(snapshot, OperationToken);
            DefinitionPrepared?.Invoke(definition, snapshot);
            Status = "已准备未完成项的续跑定义，使用新输出名称；点击“执行”重新经过 Host 授权。";
        }), Idle);
        RegenerateCommand = new AsyncRelayCommand(() => GuardAsync(() =>
        {
            var snapshot = _catalog.Capture();
            var definition = _recovery.PrepareRegenerate(snapshot);
            DefinitionPrepared?.Invoke(definition, snapshot);
            Status = "已准备重新生成未完成项：执行时读取原路径下的当前配方（内容可能已变化），成功项保留，新产物使用新名称。";
            return Task.CompletedTask;
        }), Idle);
        CleanupCommand = new AsyncRelayCommand(() => GuardAsync(async () =>
        {
            // 显式清理是应用层逐项用例；每项都有独立 Run 与 Host 删除确认，一项拒绝不阻止其他项。
            foreach (var id in _recovery.PendingCleanup)
            {
                OperationToken.ThrowIfCancellationRequested();
                var definition = _recovery.PrepareCleanup(_catalog.Capture(), id);
                var result = await _runs.RunAsync(definition, null, OperationToken);
                if (result.Cancelled) break;
            }
            Status = $"清理已结束，仍有 {_recovery.PendingCleanup.Count} 份临时源未确认释放；可重试，或由有效 marker 的 24 小时 TTL 回收。";
        }), Idle);
        AbandonCommand = new RelayCommand(() =>
        {
            _recovery.Abandon(); Refresh();
            Status = "已放弃会话恢复。成功及待核对输出保留；未释放的有效临时源按 24 小时 TTL 回收。";
        }, Idle);
    }

    public IAsyncRelayCommand AddRecipesCommand { get; }
    public IAsyncRelayCommand ChooseDirectoryCommand { get; }
    public IRelayCommand RemoveRecipeCommand { get; }
    public IRelayCommand MoveUpCommand { get; }
    public IRelayCommand MoveDownCommand { get; }
    public IAsyncRelayCommand CreateCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand RegenerateCommand { get; }
    public IAsyncRelayCommand CleanupCommand { get; }
    public IRelayCommand AbandonCommand { get; }

    public void Refresh()
    {
        Results.Clear();
        if (!_lifetime.IsClosing && !_closed) foreach (var item in _recovery.Items) Results.Add(item);
    }
    public void Cancel() { _operationCancellation?.Cancel(); _runs.Cancel(); }
    public void Close()
    {
        _closed = true; Cancel(); _recovery.Dispose(); Results.Clear(); Recipes.Clear(); OutputDirectory = "";
    }
    public void SetRunning(bool value) => IsBusy = value;
    private bool Idle() => !IsBusy && !_lifetime.IsClosing && !_closed;
    private void Move(int offset)
    {
        var destination = SelectedIndex + offset;
        if (SelectedIndex < 0 || destination < 0 || destination >= Recipes.Count) return;
        Recipes.Move(SelectedIndex, destination); SelectedIndex = destination;
    }
    private ArtWorkflowEffects Effects() => new(BlurEnabled, BlurSigma, BloomEnabled, BloomThreshold,
        BloomSigma, BloomStrength, GrainEnabled, GrainAmount, GrainSeed);
    private async Task GuardAsync(Func<Task> operation)
    {
        if (!Idle()) return;
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ClosingToken);
        IsBusy = true; BusyChanged?.Invoke(true);
        try { await operation(); }
        catch (OperationCanceledException) { Status = "操作已取消。"; }
        catch (WorkflowValidationException ex) { Status = string.Join("；", ex.Result.Issues.Select(i => i.Message)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { Status = ex.Message; }
        finally
        {
            _operationCancellation.Dispose(); _operationCancellation = null;
            IsBusy = false; BusyChanged?.Invoke(false); Refresh();
        }
    }
    partial void OnIsBusyChanged(bool value)
    {
        foreach (var command in new IRelayCommand?[] { AddRecipesCommand, ChooseDirectoryCommand, RemoveRecipeCommand,
            MoveUpCommand, MoveDownCommand, CreateCommand, ResumeCommand, RegenerateCommand, CleanupCommand, AbandonCommand })
            command?.NotifyCanExecuteChanged();
    }
}
