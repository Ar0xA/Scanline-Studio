using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.Host.Tests;

/// <summary>The root provider kept every Options view-model it created until app exit.
/// These tests pin the factory registration that replaced the tracked transient.</summary>
public sealed class OptionsViewModelLifetimeTests
{
    public static TheoryData<string> OpenCommands => new()
    {
        nameof(MainViewModel.OpenOptionsCommand),
        nameof(MainViewModel.OpenOptionsToTxTabCommand),
        nameof(MainViewModel.OpenOptionsToRadioTabCommand),
    };

    [Theory]
    [MemberData(nameof(OpenCommands))]
    public async Task OpenCommand_ViewModel_IsNotTrackedByTheRootProvider(string commandName)
    {
        // Checks the leak's actual root: the root scope's disposables list, which holds every tracked
        // instance until the provider is disposed. A GC test cannot run here: the view-model's queued
        // dispatcher posts also hold it, and only the dispatcher's owning thread can drain them.
        await using var provider = BuildServices().BuildServiceProvider();
        var mainViewModel = provider.GetRequiredService<MainViewModel>();
        OptionsWindowViewModel? opened = null;
        void Capture(OptionsWindowViewModel vm) => opened = vm;
        mainViewModel.OptionsRequested += Capture;

        var command = (System.Windows.Input.ICommand)typeof(MainViewModel).GetProperty(commandName)!.GetValue(mainViewModel)!;
        command.Execute(null);
        mainViewModel.OptionsRequested -= Capture;

        Assert.NotNull(opened);
        using (opened)
        {
            Assert.DoesNotContain(RootDisposables(provider), tracked => ReferenceEquals(tracked, opened));
        }
    }

    /// <summary>MS.DI internals (net8): <c>ServiceProvider.Root.Disposables</c>. Throws if they move, so
    /// the test fails loudly instead of passing vacuously.</summary>
    private static IEnumerable<object> RootDisposables(ServiceProvider provider)
    {
        const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var root = typeof(ServiceProvider).GetProperty("Root", Internal)?.GetValue(provider)
            ?? throw new InvalidOperationException("ServiceProvider.Root not found.");
        var disposables = root.GetType().GetProperty("Disposables", Internal)?.GetValue(root) as IEnumerable<object>
            ?? throw new InvalidOperationException("ServiceProviderEngineScope.Disposables not found.");
        return disposables.ToList();
    }

    [Fact]
    public async Task RootDisposables_SeesATrackedTransient()
    {
        // Proves RootDisposables reads the real list: a tracked transient IDisposable must appear in it.
        var services = new ServiceCollection();
        services.AddTransient<TrackedProbe>();
        await using var provider = services.BuildServiceProvider();

        var probe = provider.GetRequiredService<TrackedProbe>();

        Assert.Contains(RootDisposables(provider), tracked => ReferenceEquals(tracked, probe));
    }

    private sealed class TrackedProbe : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Factory_ReturnsFreshInstance_AndNoTrackedRegistrationExists()
    {
        await using var provider = BuildServices().BuildServiceProvider();
        var factory = provider.GetRequiredService<Func<OptionsWindowViewModel>>();

        using var first = factory();
        using var second = factory();

        Assert.NotSame(first, second);
        Assert.Null(provider.GetService<OptionsWindowViewModel>());
    }

    [Fact]
    public async Task OptionsViewModelGraph_HasNoTransientDisposable()
    {
        // ActivatorUtilities resolves the constructor's dependencies through the root provider, which
        // tracks any transient IDisposable it creates. One in this graph would bring the leak back.
        var services = BuildServices();
        await using var provider = services.BuildServiceProvider();
        var offenders = new List<string>();
        var visited = new HashSet<Type>();

        void Walk(Type implementationType)
        {
            if (!visited.Add(implementationType))
            {
                return;
            }

            var constructor = implementationType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            foreach (var parameter in constructor?.GetParameters() ?? [])
            {
                var descriptor = services.LastOrDefault(d => d.ServiceType == parameter.ParameterType && !d.IsKeyedService);
                if (descriptor is null || descriptor.Lifetime != ServiceLifetime.Transient)
                {
                    continue;
                }

                var concrete = descriptor.ImplementationType
                    ?? provider.GetService(parameter.ParameterType)?.GetType();
                if (concrete is null)
                {
                    continue;
                }

                if (typeof(IDisposable).IsAssignableFrom(concrete) || typeof(IAsyncDisposable).IsAssignableFrom(concrete))
                {
                    offenders.Add($"{parameter.ParameterType} -> {concrete}");
                }

                Walk(concrete);
            }
        }

        Walk(typeof(OptionsWindowViewModel));

        Assert.Empty(offenders);
    }

    private static ServiceCollection BuildServices() => SstvCompositionRootTests.BuildServicesWithFakes();
}
