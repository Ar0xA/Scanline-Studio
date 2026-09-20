using System.ComponentModel;
using System.Runtime.CompilerServices;
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
/// constructor injection there.
///
/// <b>Source lifetime</b>: nothing keeps a bare <c>Binding.Source</c> object alive on its own once
/// <see cref="ProvideValue"/> returns — a <see cref="TranslateBindingSource"/> handed to
/// <c>Binding.Source</c> with no other reference is eligible for collection before it is ever read,
/// permanently freezing that bound control in whatever language was active at construction time.
/// Rooted in <see cref="RootedSources"/> instead, keyed on the binding's own target object (the
/// control/Setter this <c>{loc:Translate}</c> use-site is actually attached to) via a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> — this keeps each source alive for exactly as
/// long as its target control is, and releases it automatically once that control is collected, so
/// this cannot reintroduce the leak <see cref="TranslateBindingSource.SubscribeWeakly"/>'s own weak
/// subscription exists to avoid.</summary>
public sealed class TranslateExtension : MarkupExtension
{
    /// <summary>Parameterless + settable <see cref="Key"/> exist alongside the single-string
    /// constructor solely so this extension can also be used in object-element form (e.g. inside a
    /// <c>MultiBinding</c>'s child list, where the usual <c>{loc:Translate ...}</c> curly-brace/
    /// positional-arg form doesn't apply) -- every existing curly-brace call site keeps working
    /// unchanged via the constructor overload below.</summary>
    public TranslateExtension()
    {
    }

    public TranslateExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public object[] Args { get; set; } = [];

    /// <summary>Keyed on each <c>{loc:Translate}</c> use-site's own target object (a control, or a
    /// <c>Setter</c> for a Style-level use), never on <see cref="ILocalizationService"/> itself —
    /// that's the long-lived singleton this whole indirection exists to avoid pinning anything
    /// against. A weak key with no matching finalizer/cleanup callback is exactly
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/>'s intended shape: the entry (and every
    /// <see cref="TranslateBindingSource"/> in its list) is released automatically the moment the
    /// target object itself becomes unreachable, with no explicit Dispose/unsubscribe needed here.</summary>
    private static readonly ConditionalWeakTable<object, List<TranslateBindingSource>> RootedSources = new();

    /// <summary>Rare fallback for a use-site where Avalonia's XAML loader doesn't supply
    /// <see cref="IProvideValueTarget"/> (no target object to key <see cref="RootedSources"/> on) --
    /// a real permanent root, not weak. Accepted: uncommon enough in practice that a small permanent
    /// list beats silently reproducing the original bug for those sites.</summary>
    private static readonly List<TranslateBindingSource> UnrootedFallbackSources = [];

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var localization = App.Services?.GetRequiredService<ILocalizationService>()
            ?? throw new InvalidOperationException(
                "ILocalizationService is not available -- App.Services was never set by ScanlineStudio.Host.");

        var source = new TranslateBindingSource(localization, Key, Args);

        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetObject: { } targetObject })
        {
            RootedSources.GetOrCreateValue(targetObject).Add(source);
        }
        else
        {
            lock (UnrootedFallbackSources)
            {
                UnrootedFallbackSources.Add(source);
            }
        }

        return new Binding(nameof(TranslateBindingSource.Value))
        {
            Source = source,
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
