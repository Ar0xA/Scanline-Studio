using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Localization;
using Yoniq.Abstractions.Sstv;
using Yoniq.Application;
using Yoniq.UI.Imaging;
using Yoniq.UI.Services;

namespace Yoniq.UI.ViewModels;

/// <summary>Real pane #3 of 3 (Phase-3 plan decision #9) -- "basic TX button"
/// (spec/14-roadmap.md's Phase 3 bullet), not the Phase-4 crop/resize/filter/overlay tooling.</summary>
public sealed partial class TxControlsPaneViewModel : Tool
{
    private readonly ISstvSessionService _sstvSession;
    private readonly IImageFileLoader _imageFileLoader;
    private readonly IFilePickerService _filePickerService;
    private readonly ILocalizationService _localization;

    private IImageSource? _loadedImage;

    [ObservableProperty]
    private SstvModeDefinition? _selectedMode;

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private string? _selectedFileName;

    [ObservableProperty]
    private bool _isTransmitting;

    [ObservableProperty]
    private string? _errorMessage;

    public TxControlsPaneViewModel(
        ISstvSessionService sstvSession,
        IImageFileLoader imageFileLoader,
        IFilePickerService filePickerService,
        ILocalizationService localization)
    {
        _sstvSession = sstvSession;
        _imageFileLoader = imageFileLoader;
        _filePickerService = filePickerService;
        _localization = localization;

        Id = "TxControls";
        Title = localization.GetString("Panes.TxControls.Title");
        localization.CultureChanged += () => Title = localization.GetString("Panes.TxControls.Title");

        AvailableModes = sstvSession.AvailableModes;
        _selectedMode = AvailableModes.Count > 0 ? AvailableModes[0] : null;
    }

    public IReadOnlyList<SstvModeDefinition> AvailableModes { get; }

    [RelayCommand]
    private async Task SelectImageAsync()
    {
        ErrorMessage = null;
        if (SelectedMode is not { } mode)
        {
            return;
        }

        var path = await _filePickerService.PickImageFileAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            _loadedImage = await _imageFileLoader.LoadAsync(path, mode.ImageWidth, mode.ImageHeight);
            PreviewImage = ImageSourceBitmapConverter.ToBitmap(_loadedImage);
            SelectedFileName = Path.GetFileName(path);
        }
        catch (Exception)
        {
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
        }
    }

    private bool CanTransmit() => _loadedImage is not null && !IsTransmitting;

    [RelayCommand(CanExecute = nameof(CanTransmit))]
    private async Task TransmitAsync()
    {
        if (_loadedImage is not { } image || SelectedMode is not { } mode)
        {
            return;
        }

        ErrorMessage = null;
        IsTransmitting = true;
        TransmitCommand.NotifyCanExecuteChanged();
        try
        {
            await _sstvSession.TransmitAsync(mode, image);
        }
        catch (Exception)
        {
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.TransmitFailed");
        }
        finally
        {
            IsTransmitting = false;
            TransmitCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedModeChanged(SstvModeDefinition? value)
    {
        // A newly-selected mode invalidates any already-loaded image (it was resized to the OLD
        // mode's dimensions) -- clearing rather than silently transmitting a wrongly-sized image.
        _loadedImage = null;
        PreviewImage = null;
        SelectedFileName = null;
        TransmitCommand.NotifyCanExecuteChanged();
    }
}
