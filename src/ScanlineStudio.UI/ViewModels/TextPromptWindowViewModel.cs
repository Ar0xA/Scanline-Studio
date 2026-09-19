using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Configurations-preset backlog, Phase 4 (2026-08-28) -- a small reusable "enter a name"
/// prompt (Title/Message/one TextBox/OK-Cancel), the FIRST dialog in this codebase that returns a
/// typed result to its caller rather than being a pure acknowledgement or a self-contained editor.
/// Constructed with per-invocation parameters (title/message/prefill), so it is always `new`'d
/// directly by its opener (<c>MainWindow.axaml.cs</c>'s Configurations-menu code, or
/// <c>RxHistoryPaneViewModel</c>'s Gallery "Add note" flow), never DI-registered -- same reasoning
/// as <c>HamlibLibraryReloadFailedDialogViewModel</c>'s own per-attempt-payload constructor.
///
/// Generalized 2026-09-19 (Gallery "Add note" context-menu request) -- was hard-wired to
/// <c>IConfigurationPresetStore.TryValidatePresetName</c> directly, which made it unusable for a
/// free-text note with no preset-naming rules at all. Now takes an optional
/// <paramref name="validate"/> delegate instead, defaulting to "always valid, no error" -- the
/// Configurations caller passes one wrapping its own store call (same validation source as before,
/// same "SAME rule the store itself enforces, not a hand-copied second blocklist" property), the
/// Gallery note caller omits it entirely since any text is a valid note. Manual-verification finding
/// this preserved: the store's own error text says "Preset name..." (its own internal vocabulary) --
/// shown verbatim, that would violate that feature's own "never say 'Preset' in a user-facing string,
/// always 'Configuration'" rule (plan point 1), so the CALLER's delegate is responsible for its own
/// user-facing error text, not this class. A COLLISION against an existing preset name is
/// deliberately NOT checked here (this VM has no store-state visibility of its own beyond the
/// validation call) -- that surfaces on the caller's own dialog after this one closes, per that
/// feature's own plan doc.</summary>
public sealed partial class TextPromptWindowViewModel : ObservableObject
{
    private readonly Func<string, (bool IsValid, string? ErrorMessage)> _validate;

    public TextPromptWindowViewModel(string title, string message, string prefillText = "", Func<string, (bool IsValid, string? ErrorMessage)>? validate = null)
    {
        _validate = validate ?? (static _ => (true, null));
        Title = title;
        Message = message;
        _text = prefillText;
        ValidateText();
    }

    public string Title { get; }

    public string Message { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    private string _text;

    partial void OnTextChanged(string value) => ValidateText();

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Round-1 code-review fix: computed directly from <see cref="Text"/> (already re-raised
    /// via <c>_text</c>'s own <c>[NotifyPropertyChangedFor(nameof(IsValid))]</c>), NOT derived from
    /// <see cref="ErrorMessage"/> -- the two used to be coupled with no explicit notification wired
    /// between them, correct only because nothing else ever wrote <see cref="ErrorMessage"/>. Now
    /// independent: <see cref="ErrorMessage"/> is purely a DISPLAY concern (see
    /// <see cref="ValidateText"/>'s own doc comment for why it stays <see langword="null"/> on empty
    /// text even though empty is still invalid).</summary>
    public bool IsValid => _validate(Text).IsValid;

    /// <summary>Fires with the typed, already-validated text on OK; <see langword="null"/> on
    /// Cancel/close -- the caller (<c>MainWindow.axaml.cs</c>'s Configurations-menu code) awaits
    /// <c>ShowDialog&lt;string?&gt;</c> and reads whichever this raises last.</summary>
    public event Action<string?>? RequestClose;

    [RelayCommand(CanExecute = nameof(IsValid))]
    private void Ok() => RequestClose?.Invoke(Text);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(null);

    /// <summary>Round-1 code-review fix: empty text is still INVALID (<see cref="IsValid"/> stays
    /// false, OK stays disabled) but shows no red error text -- the original version showed a
    /// validation error before the user had typed anything at all (Save-As-New's own empty prefill),
    /// which reads as the dialog complaining about a field the user hasn't touched yet. A disabled OK
    /// button is self-explanatory on its own.</summary>
    private void ValidateText()
    {
        ErrorMessage = Text.Length == 0 || IsValid ? null : _validate(Text).ErrorMessage;
    }
}
