using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ScanlineStudio.Abstractions.Localization;

namespace ScanlineStudio.UI.Tests;

/// <summary>Live-locale-switch review (2026-09-20): the fix wired across MainViewModel,
/// RadioStatusViewModel, RxHistoryPaneViewModel, LogbookPaneViewModel, WaterfallPaneViewModel,
/// DecoderTracePaneViewModel, TxControlsPaneViewModel, RxImagePaneViewModel,
/// TxImageEditorPaneViewModel, and OptionsWindowViewModel all rests on one assumption a yoniq-auditor
/// plan-review could not verify from source (NuGet-only Avalonia reference, no local clone): that
/// Avalonia's binding engine treats <c>OnPropertyChanged(string.Empty)</c> (a
/// <see cref="System.ComponentModel.PropertyChangedEventArgs"/> with an empty/null property name) as
/// "every bound property on this instance may have changed, please refresh" -- the same convention
/// WPF uses. This test settles it directly: a real <see cref="TextBlock"/>, bound via a real
/// <c>{Binding}</c> to a get-only computed property, must show the new-language text after
/// <see cref="ILocalizationService.CultureChanged"/> fires and the view-model's handler calls
/// <c>OnPropertyChanged(string.Empty)</c> -- with no per-property <c>OnPropertyChanged(nameof(...))</c>
/// call anywhere.</summary>
public sealed class CultureChangedBindingRefreshTests
{
    [AvaloniaFact]
    public void OnPropertyChangedEmptyString_RefreshesARealComputedPropertyBinding()
    {
        var localization = new SwitchableLocalizationService();
        var viewModel = new GreetingViewModel(localization);

        var textBlock = new TextBlock { DataContext = viewModel };
        textBlock.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(GreetingViewModel.Greeting)));

        var window = new Window { Content = textBlock };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Hello", textBlock.Text);

        localization.SwitchTo("Hallo");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Hallo", textBlock.Text);
    }

    private sealed class GreetingViewModel : ObservableObject
    {
        private readonly ILocalizationService _localization;

        public GreetingViewModel(ILocalizationService localization)
        {
            _localization = localization;
            localization.CultureChanged += OnCultureChanged;
        }

        public string Greeting => _localization.GetString("Greeting");

        private void OnCultureChanged() => OnPropertyChanged(string.Empty);
    }

    /// <summary>Deliberately not <see cref="Fakes.FakeLocalizationService"/> (that fake's
    /// <c>GetString</c> just echoes the key back, with no way to switch what it returns) -- this
    /// test needs a real "two languages, GetString returns a different value after CultureChanged"
    /// round trip, without depending on the real JsonLocalizationService's file-loading machinery
    /// (which this test isn't exercising).</summary>
    private sealed class SwitchableLocalizationService : ILocalizationService
    {
        private string _greeting = "Hello";

        public IReadOnlyList<System.Globalization.CultureInfo> AvailableCultures { get; } =
            [System.Globalization.CultureInfo.GetCultureInfo("en")];

        public System.Globalization.CultureInfo CurrentCulture { get; } =
            System.Globalization.CultureInfo.GetCultureInfo("en");

        public event Action? CultureChanged;

        public Task SetCultureAsync(System.Globalization.CultureInfo culture, CancellationToken ct = default) =>
            Task.CompletedTask;

        public string GetString(string key, params object[] args) => _greeting;

        public void SwitchTo(string greeting)
        {
            _greeting = greeting;
            CultureChanged?.Invoke();
        }
    }
}
