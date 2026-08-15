using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>One editable row in the Options dialog's Forwarding tab -- see
/// <see cref="OptionsWindowViewModel.AdifUdpDestinations"/>. Deliberately generic (Enabled/Name/
/// Host/Port only, no per-service branded fields) -- see <c>IAdifUdpStreamer</c>'s own doc comment
/// for why. Mirrors <see cref="OverlayElementViewModel"/>'s established list-editable-row shape in
/// this codebase: <see cref="RemoveCommand"/> is injected once by the owning
/// <see cref="OptionsWindowViewModel"/> at row-creation time, NOT bound in XAML via a
/// `$parent[ItemsControl].((vm:X)DataContext)...` path cast -- that pattern throws
/// `ArgumentException: Unable to resolve type` the first time the row's `DataTemplate` is realized
/// in this project's non-compiled-binding setup (see <see cref="OverlayElementViewModel.RemoveCommand"/>'s
/// own doc comment for the full explanation).</summary>
public sealed partial class AdifUdpDestinationRowViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string? _name;

    [ObservableProperty]
    private string? _host;

    [ObservableProperty]
    private int? _port;

    /// <summary>Set once by <see cref="OptionsWindowViewModel"/> at row-creation time, same pattern
    /// as <see cref="OverlayElementViewModel.RemoveCommand"/>.</summary>
    public IRelayCommand? RemoveCommand { get; init; }

    public AdifUdpDestination ToDestination() => new()
    {
        Enabled = Enabled,
        Name = Name,
        Host = Host,
        Port = Port,
    };
}
