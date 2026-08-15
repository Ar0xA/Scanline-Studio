using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Backs Help > About (spec/18-path-to-1.0.md High item 5). Constructed directly with
/// <c>new</c> (no DI dependencies) -- same reasoning as <see cref="QsoLinkWindowViewModel"/>'s own
/// doc comment: this VM only ever needs read-only assembly metadata, nothing injectable.
///
/// Reads <see cref="Assembly.GetEntryAssembly"/> (the running <c>ScanlineStudio.Host</c>
/// executable), not this assembly (<c>ScanlineStudio.UI</c>) -- both carry the same
/// Directory.Build.props-stamped Version/Product/Copyright and the same SDK-auto-appended git
/// commit SHA (every assembly in the solution is built from the same commit), but the entry
/// assembly is the semantically correct one to attribute a running build to.</summary>
public sealed partial class AboutWindowViewModel : ObservableObject
{
    public string ApplicationName { get; }

    /// <summary>The SDK automatically appends a full git commit SHA build-metadata suffix
    /// (`+&lt;sha&gt;`) to <see cref="AssemblyInformationalVersionAttribute"/> once it detects a
    /// `.git` directory -- confirmed empirically during this feature's own implementation (no
    /// custom git-invoking MSBuild target needed, see ScanlineStudio.Host.csproj's own doc
    /// comment on that dead end). Falls back to the plain assembly version if the informational
    /// attribute is somehow absent (e.g. a non-SDK build), never blank.</summary>
    public string VersionDisplay { get; }

    public string Copyright { get; }

    public AboutWindowViewModel() : this(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
    {
    }

    /// <summary>Test-only entry point in practice, but PUBLIC not internal (code-review finding
    /// caught this the other way first): this project has no <c>InternalsVisibleTo</c> wired up
    /// anywhere (see <see cref="ScanlineStudio.UI.Controls.WaterfallPalette"/>'s own doc comment
    /// for the established precedent/reasoning), so an <c>internal</c> constructor here would have
    /// been silently inaccessible from <c>ScanlineStudio.UI.Tests</c> entirely. The parameterless
    /// constructor's <see cref="Assembly.GetEntryAssembly"/> is the test RUNNER's own assembly
    /// under xunit, not <c>ScanlineStudio.Host</c> or even <c>ScanlineStudio.UI</c> -- letting a
    /// test inject <c>typeof(AboutWindowViewModel).Assembly</c> (this project's own, which DOES
    /// carry the real Directory.Build.props-stamped Product/Version/Copyright) lets tests assert
    /// the actual real-app values instead of only "is this non-blank."</summary>
    public AboutWindowViewModel(Assembly assembly)
    {
        ApplicationName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Scanline Studio";
        VersionDisplay = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        Copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
    }

    /// <summary>Same convention as <see cref="QsoLinkWindowViewModel.RequestClose"/> -- the View's
    /// code-behind subscribes <c>vm.RequestClose += Close;</c>.</summary>
    public event Action? RequestClose;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
