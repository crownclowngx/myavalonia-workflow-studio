using Avalonia.Controls;
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
    }

    private sealed class CapturingRegistration : IPluginRegistration, IWorkflowActionRegistration
    {
        public PluginId PluginId { get; } = PluginIds.Plugin;
        public IServiceCollection Services { get; } = new ServiceCollection();
        internal bool GatewayRequested { get; private set; }
        internal DocumentDescriptor? DocumentDescriptor { get; private set; }
        internal DocumentDescriptor? PersistableDocumentDescriptor { get; private set; }
        internal Type? DocumentModel { get; private set; }
        internal Type? DocumentView { get; private set; }

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
    }
}
