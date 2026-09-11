using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using WorkflowStudio.Constants;
using WorkflowStudio.Features.Main;
using WorkflowStudio.Plugin;
using WorkflowStudio.Workflows;
using Xunit;

namespace WorkflowStudio.Tests;

public sealed class PluginCompositionTests
{
    [Fact]
    public void Module只注册一个非持久化Document并请求Gateway()
    {
        var registration = new CapturingRegistration();

        new WorkflowStudioModule().Configure(registration);

        Assert.True(registration.GatewayRequested);
        Assert.Equal(PluginIds.StudioDocument, registration.DocumentDescriptor!.DocumentTypeId);
        Assert.Equal(typeof(MainDocument), registration.DocumentModel);
        Assert.Equal(typeof(MainView), registration.DocumentView);
        Assert.Null(registration.PersistableDocumentDescriptor);
        Assert.Equal(3, registration.Commands.Count);
        Assert.Equal(3, registration.MenuContributions.Count);
        Assert.Equal(3, registration.KeyBindingContributions.Count);
        Assert.All(
            registration.Commands,
            command => Assert.Equal(PluginIds.StudioDocument, command.TargetDocumentTypeId));
        Assert.All(
            registration.MenuContributions,
            contribution =>
            {
                Assert.Equal(WorkbenchMenuLocations.ToolsShared, contribution.LocationId);
                Assert.Equal("workflow", contribution.Group);
                Assert.Equal(MenuCommandTargetUnavailableBehavior.Hide,
                    contribution.TargetUnavailableBehavior);
            });
        Assert.Collection(
            registration.KeyBindingContributions.OrderBy(item => item.PlacementId.Value),
            item =>
            {
                Assert.Equal(PluginIds.CancelWorkflow, item.CommandId);
                Assert.Equal(Key.F5, item.Key);
                Assert.Equal(KeyModifiers.Shift, item.Modifiers);
            },
            item =>
            {
                Assert.Equal(PluginIds.RunWorkflow, item.CommandId);
                Assert.Equal(Key.F5, item.Key);
                Assert.Equal(KeyModifiers.None, item.Modifiers);
            },
            item =>
            {
                Assert.Equal(PluginIds.ValidateWorkflow, item.CommandId);
                Assert.Equal(Key.F6, item.Key);
                Assert.Equal(KeyModifiers.None, item.Modifiers);
            });
    }

    [Fact]
    public void 公共组合入口可以在严格Scope验证下构造Document()
    {
        var services = new ServiceCollection();
        services.AddWorkflowStudioServices();
        services.AddSingleton<IWorkflowActionGateway>(new MutableGateway());
        services.AddScoped<IDocumentLifetime>(_ => new TestLifetime());
        services.AddScoped<MainDocument>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        using var scope = provider.CreateScope();

        var document = scope.ServiceProvider.GetRequiredService<MainDocument>();

        Assert.NotNull(document);
        using var otherScope = provider.CreateScope();
        Assert.NotSame(
            scope.ServiceProvider.GetRequiredService<ISessionSecretStore>(),
            otherScope.ServiceProvider.GetRequiredService<ISessionSecretStore>());
    }

    [Fact]
    public void 稳定Plugin与Document身份符合G3冻结值()
    {
        Assert.Equal("myavalonia.plugin.workflow-studio", PluginIds.Plugin.Value);
        Assert.Equal("myavalonia.plugin.workflow-studio.document.studio", PluginIds.StudioDocument.Value);
        Assert.Equal("myavalonia.plugin.workflow-studio.command.validate", PluginIds.ValidateWorkflow.Value);
        Assert.Equal("myavalonia.plugin.workflow-studio.command.run", PluginIds.RunWorkflow.Value);
        Assert.Equal("myavalonia.plugin.workflow-studio.command.cancel", PluginIds.CancelWorkflow.Value);
    }

    private sealed class CapturingRegistration :
        IPluginRegistration, IPluginIconRegistration,
        IWorkflowActionRegistration,
        IWorkbenchCommandRegistration
    {
        // V6.1：预览/测试只保留本次组合的纯图标数据；不使用 Host 的全局注册表或缓存。
        // 对重复名称和非法名称直接报错，避免预览吞掉正式 Host 会拒绝的声明。
        private readonly Dictionary<string, VectorIconDefinition> _previewIcons = new(StringComparer.Ordinal);
        public string AddIcon(string localName, VectorIconDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (localName is null || !System.Text.RegularExpressions.Regex.IsMatch(localName, @"\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\z"))
                throw new ArgumentException("图标名称必须使用小写字母、数字及单个连字符分段。", nameof(localName));
            var reference = $"plugin:{PluginId.Value}/{localName}";
            _previewIcons.Add(reference, definition);
            return reference;
        }


        public PluginId PluginId { get; } = PluginIds.Plugin;
        public IServiceCollection Services { get; } = new ServiceCollection();
        internal bool GatewayRequested { get; private set; }
        internal DocumentDescriptor? DocumentDescriptor { get; private set; }
        internal DocumentDescriptor? PersistableDocumentDescriptor { get; private set; }
        internal Type? DocumentModel { get; private set; }
        internal Type? DocumentView { get; private set; }
        internal List<(CommandDescriptor Descriptor, DocumentTypeId TargetDocumentTypeId)> Commands
        { get; } = [];
        internal List<MenuCommandContributionDescriptor> MenuContributions { get; } = [];
        internal List<KeyBindingContributionDescriptor> KeyBindingContributions { get; } = [];

        public void UseLifecycle<TLifecycle>() where TLifecycle : class, IPluginLifecycle =>
            throw new NotSupportedException();

        public void AddDocument<TDocument, TView>(DocumentDescriptor descriptor)
            where TDocument : class, IPluginDocument
            where TView : Control, new()
        {
            DocumentDescriptor = descriptor;
            DocumentModel = typeof(TDocument);
            DocumentView = typeof(TView);
        }

        public void AddPersistableDocument<TDocument, TView>(DocumentDescriptor descriptor)
            where TDocument : class, IPersistablePluginDocument
            where TView : Control, new() => PersistableDocumentDescriptor = descriptor;

        public void AddTool<TTool, TView>(ToolDescriptor descriptor)
            where TTool : class
            where TView : Control, new() => throw new NotSupportedException();

        public void AddWorkflowAction<THandler>(WorkflowActionDescriptor descriptor)
            where THandler : class, IWorkflowActionHandler => throw new NotSupportedException();

        public void UseWorkflowActionGateway() => GatewayRequested = true;

        public void AddDocumentCommand(
            CommandDescriptor descriptor,
            DocumentTypeId targetDocumentTypeId) =>
            Commands.Add((descriptor, targetDocumentTypeId));

        public void AddMenuCommandContribution(MenuCommandContributionDescriptor descriptor) =>
            MenuContributions.Add(descriptor);

        public void AddKeyBindingContribution(KeyBindingContributionDescriptor descriptor) =>
            KeyBindingContributions.Add(descriptor);
    }
}
