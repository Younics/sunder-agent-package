extern alias AgentApp;
extern alias AnthropicApp;
extern alias BuilderApp;
extern alias DockerApp;
extern alias GeminiApp;
extern alias LMStudioApp;
extern alias OpenAiApp;

using System.Text.Json.Serialization;
using System.Text.Json;
using AgentApp::Sunder.Package.Agent.PackageViews;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BuilderApp::Sunder.Package.Agent.Builder;
using DockerApp::Sunder.Package.Agent.Execution.Docker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAiApp::Sunder.Package.Agent.Provider.OpenAI;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Services;
using Xunit;
using AnthropicSettingsView = AnthropicApp::Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsView;
using GeminiSettingsView = GeminiApp::Sunder.Package.Agent.Provider.Gemini.GeminiSettingsView;
using IOpenAiManagementService = OpenAiApp::Sunder.Generated.Packages.Younics_Ai_Agent_Provider_Openai_Services.IManagementService;
using LMStudioSettingsView = LMStudioApp::Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsView;
using OpenAiSettingsView = OpenAiApp::Sunder.Package.Agent.Provider.OpenAI.OpenAiSettingsView;
using OpenAiSettingsViewModel = OpenAiApp::Sunder.Package.Agent.Provider.OpenAI.OpenAiSettingsViewModel;
using static Sunder.Package.Agent.Presentation.Tests.RenderedBuilderFixture;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed partial class ProviderSettingsRenderedTests
{
    [AvaloniaTheory]
    [InlineData("agent")]
    [InlineData("builder")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("lmstudio")]
    [InlineData("openai")]
    public async Task SettingsView_UiRetirementThenBackgroundContainerDisposalIsIdempotent(string package)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Control>(_ => package switch
        {
            "agent" => new AgentPermissionsView(),
            "builder" => new BuilderSettingsView(),
            "anthropic" => new AnthropicSettingsView(),
            "gemini" => new GeminiSettingsView(),
            "lmstudio" => new LMStudioSettingsView(),
            "openai" => new OpenAiSettingsView(),
            _ => throw new ArgumentOutOfRangeException(nameof(package)),
        });
        await using var provider = services.BuildServiceProvider();
        var view = provider.GetRequiredService<Control>();
        view.DataContext = new object();

        // The shell retires cached controls on its UI dispatcher before it drains
        // the generated composition's singleton service container off-thread.
        Assert.IsAssignableFrom<IDisposable>(view).Dispose();
        Assert.Null(view.DataContext);
        await Task.Run(async () =>
        {
            Assert.False(Dispatcher.UIThread.CheckAccess());
            await provider.DisposeAsync();
        });
    }

    [AvaloniaFact]
    public async Task DockerSettingsView_PreparationHydratesBeforePresentation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var runtime = new BlockingDockerSettingsRuntimeClient();
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(scope.Context);
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
        services.AddSingleton<IBackgroundTaskQueue, NoOpBackgroundTaskQueue>();
        services.AddTransient(provider => (DockerExecutionSettingsViewModel)AgentRoleBoundary.Create(
            typeof(DockerExecutionSettingsViewModel), runtime.Service, provider.GetRequiredService<IBackgroundTaskQueue>(), null, null));
        new DockerApp::Sunder.Generated.Package.AppModule()
            .ConfigureAppServices(services, scope.Context);
        await using var provider = services.BuildServiceProvider();
        using var view = provider.GetRequiredService<DockerExecutionSettingsView>();
        var viewModel = Assert.IsType<DockerExecutionSettingsViewModel>(view.DataContext);
        var source = new Border();
        var candidateHost = new ContentControl
        {
            Content = view,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = new Grid { Children = { source, candidateHost } },
        };
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var lifecycle = Assert.IsAssignableFrom<IPackageViewNavigationPreparationTarget>(viewModel);
            var context = new PackageViewNavigationContext(
                "younics.ai.agent.execution.docker.settings",
                new Dictionary<string, string?>());

            var preparation = lifecycle.PrepareNavigationAsync(context).AsTask();
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(preparation.IsCompleted);
            Assert.True(viewModel.IsLoading);
            Assert.False(viewModel.CanMutate);
            Assert.Empty(viewModel.Images);
            Assert.Equal(0, candidateHost.Opacity);
            Assert.False(candidateHost.IsHitTestVisible);

            runtime.Complete(
                "450",
                "/usr/local/bin/docker",
                [
                    new DockerImageDefinition(
                        "ubuntu:24.04",
                        DockerImageStatus.Ready,
                        DateTimeOffset.UtcNow,
                        null),
                ],
                7);

            Assert.True(await preparation.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(viewModel.IsReady);
            Assert.True(viewModel.CanMutate);
            Assert.Equal("450", viewModel.TimeoutSeconds);
            Assert.Equal("/usr/local/bin/docker", viewModel.DockerCliPath);
            Assert.Equal("ubuntu:24.04", Assert.Single(viewModel.Images).ImageReference);
            Assert.Equal(0, candidateHost.Opacity);

            candidateHost.Opacity = 1;
            candidateHost.IsHitTestVisible = true;
            await lifecycle.OnNavigationPresentedAsync(context);

            Assert.Equal(1, candidateHost.Opacity);
            Assert.True(candidateHost.IsHitTestVisible);
            Assert.Equal(1, runtime.InvocationCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SecretFieldStyle_MasksProviderApiKeyInput()
    {
        var view = new OpenAiSettingsView();
        var secretField = new TextBox();
        secretField.Classes.Add("secret-field");
        view.Content = secretField;
        var window = new Window { Content = view };

        window.Show();
        Assert.Equal('*', secretField.PasswordChar);
        window.Close();
    }

    [AvaloniaFact]
    public void OpenAiSettingsView_HasBoundAuthorizeButton()
    {
        using var scope = RegressionTestPackageScope.Create();
        var viewModel = new OpenAiSettingsViewModel(
            scope.Context,
            new ConnectedOpenAiManagementClient(isConnected: false));
        using var view = new OpenAiSettingsView(viewModel);

        var button = view.FindControl<Button>("AuthorizeButton");

        Assert.NotNull(button);
        Assert.Same(viewModel.AuthorizeCommand, button.Command);
        Assert.Equal("Authorize with ChatGPT Plus/Pro", button.Content);
    }

    [AvaloniaFact]
    public async Task OpenAiSettingsView_InitializesPersistedRuntimeAuthStatus()
    {
        using var scope = RegressionTestPackageScope.Create();
        var management = new ConnectedOpenAiManagementClient();
        var viewModel = new OpenAiSettingsViewModel(scope.Context, management);
        using var view = new OpenAiSettingsView(viewModel);

        Assert.Equal(0, management.InvocationCount);
        var lifecycle = Assert.IsAssignableFrom<IPackageViewNavigationPreparationTarget>(view.DataContext);
        Assert.True(await lifecycle.PrepareNavigationAsync(new PackageViewNavigationContext(
            "settings:younics.ai.agent.provider.openai",
            new Dictionary<string, string?>())));

        Assert.True(management.InvocationCount > 0);
        Assert.Equal("Connected, active", viewModel.CodexStatusLabel);
        Assert.True(viewModel.CanDisconnectAction);
        Assert.Equal("Reauthorize with ChatGPT Plus/Pro", viewModel.AuthorizationButtonLabel);
    }

    private sealed class ConnectedOpenAiManagementClient(bool isConnected = true)
        : IOpenAiManagementService
    {
        private readonly DateTimeOffset _expiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);

        public int InvocationCount { get; private set; }

        public ValueTask<OpenAiAuthOperationResult> GetAuthStatusAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.FromResult(new OpenAiAuthOperationResult(
                isConnected,
                isConnected,
                isConnected ? _expiresAtUtc : null));
        }

        public ValueTask<OpenAiAuthOperationResult> DisconnectAuthAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.FromResult(new OpenAiAuthOperationResult(false, false, null));
        }

        public ValueTask<bool> GetCredentialStatusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.FromResult(false);
        }

        public ValueTask<bool> SetCredentialAsync(
            string credential,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ClearCredentialAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.FromResult(false);
        }
    }

    private sealed class BlockingDockerSettingsRuntimeClient
    {
        private int _invocationCount;
        private readonly TaskCompletionSource _responseReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _timeoutSeconds;
        private string? _dockerCliPath;
        private IReadOnlyList<DockerImageDefinition>? _images;
        private long _catalogRevision;

        public BlockingDockerSettingsRuntimeClient()
        {
            var service = (DockerPresentationServiceProxy)System.Reflection.DispatchProxy.Create(
                typeof(DockerExecutionSettingsViewModel).Assembly.GetType(
                    "Sunder.Generated.Packages.Younics_Ai_Agent_Execution_Docker_Services.IPresentationService", true)!,
                typeof(DockerPresentationServiceProxy));
            service.Runtime = this;
            Service = service;
        }

        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public object Service { get; }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(
            string timeoutSeconds,
            string dockerCliPath,
            IReadOnlyList<DockerImageDefinition> images,
            long catalogRevision)
        {
            _timeoutSeconds = timeoutSeconds;
            _dockerCliPath = dockerCliPath;
            _images = images;
            _catalogRevision = catalogRevision;
            _responseReady.TrySetResult();
        }

        public async ValueTask<SunderContent> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            Started.TrySetResult();
            await _responseReady.Task.WaitAsync(cancellationToken);
            var response = new DockerExecutionOperationResponse(
                _timeoutSeconds,
                _dockerCliPath,
                _images,
                CatalogRevision: _catalogRevision);
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = false,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
            };
            return SunderContent.FromBytes(JsonSerializer.SerializeToUtf8Bytes(response, json), "application/json", "docker-catalog.json");
        }

        private class DockerPresentationServiceProxy : System.Reflection.DispatchProxy
        {
            internal BlockingDockerSettingsRuntimeClient Runtime { get; set; } = null!;

            protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? arguments)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);
                if (targetMethod.Name == "GetSettingsAsync")
                {
                    var cancellationToken = arguments?.OfType<CancellationToken>().SingleOrDefault() ?? default;
                    return Runtime.GetSettingsAsync(cancellationToken);
                }

                throw new NotSupportedException($"Unexpected Docker presentation method '{targetMethod.Name}'.");
            }
        }
    }
}
