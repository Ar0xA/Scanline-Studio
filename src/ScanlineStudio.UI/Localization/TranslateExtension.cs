using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using ScanlineStudio.Abstractions.Localization;

namespace ScanlineStudio.UI.Localization;

/// <summary>The <c>{loc:Translate ...}</c> markup extension — see spec/10-localization.md. Backs
/// every user-visible string; re-evaluates live on <see cref="ILocalizationService.CultureChanged"/>,
/// no restart.
///
/// <b>DI-into-XAML note (spec/01-architecture.md's own "no service locator" rule)</b>: Avalonia's
/// XAML loader instantiates markup extensions itself with no constructor-injection hook, so this is
/// the ONE other sanctioned reader of <see cref="App.Services"/> besides its own documented
/// bootstrap use in <c>App.axaml.cs</c>. View-model code must never do this — always real
/// constructor injection there.</summary>
public sealed class TranslateExtension : MarkupExtension
{
    private readonly string _key;

    public TranslateExtension(string key)
    {
        _key = key;
    }

    public object[] Args { get; set; } = [];

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var localization = App.Services?.GetRequiredService<ILocalizationService>()
            ?? throw new InvalidOperationException(
                "ILocalizationService is not available -- App.Services was never set by ScanlineStudio.Host.");

        return new Binding(nameof(TranslateBindingSource.Value))
        {
            Source = new TranslateBindingSource(localization, _key, Args),
        };
    }
}

/// <summary>Adapts one <c>{loc:Translate}</c> use-site to <see cref="INotifyPropertyChanged"/> so a
/// plain <see cref="Binding"/> can re-pull <see cref="Value"/> when the culture changes.
///
/// <b>Lifetime note</b>: <see cref="ILocalizationService"/> is a long-lived singleton, so a plain
/// <c>CultureChanged += OnCultureChanged</c> here would let its invocation list pin every
/// <see cref="TranslateBindingSource"/> ever created for the rest of the process — a real leak once
/// dockable panes are opened/closed repeatedly (exactly this project's own UI shape). Subscribes via
/// a weak-reference indirection instead: the closure the service holds captures only a
/// <see cref="WeakReference{T}"/>, never <c>this</c>, and unsubscribes itself the first time it finds
/// the target already collected.</summary>
internal sealed class TranslateBindingSource : INotifyPropertyChanged
{
    private readonly ILocalizationService _localization;
    private readonly string _key;
    private readonly object[] _args;

    public TranslateBindingSource(ILocalizationService localization, string key, object[] args)
    {
        _localization = localization;
        _key = key;
        _args = args;
        SubscribeWeakly(localization, this);
    }

    public string Value => _localization.GetString(_key, _args);

    public event PropertyChangedEventHandler? PropertyChanged;

    private static void SubscribeWeakly(ILocalizationService localization, TranslateBindingSource target)
    {
        var weakTarget = new WeakReference<TranslateBindingSource>(target);
        Action? handler = null;
        handler = () =>
        {
            if (weakTarget.TryGetTarget(out var alive))
            {
                alive.PropertyChanged?.Invoke(alive, new PropertyChangedEventArgs(nameof(Value)));
            }
            else
            {
                localization.CultureChanged -= handler;
            }
        };
        localization.CultureChanged += handler;
    }
}
