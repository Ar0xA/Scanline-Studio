using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Converters;
using ScanlineStudio.UI.Imaging;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.ViewModels;

public sealed partial class TxImageEditorPaneViewModel
{
    /// <summary>TX editor gap-items plan, item 2 -- the ONE place that atomically replaces the whole
    /// selection (single element, empty, or a real 2+ group). Every raw
    /// <c>SelectedOverlayElement = x</c> assignment elsewhere in this file (unchanged, still valid)
    /// relies on <see cref="OnSelectedOverlayElementChanged"/>'s own self-healing collapse instead --
    /// this method exists for the cases that self-healing CAN'T cover: building/shrinking an actual
    /// group, and re-selecting an element that's ALREADY the primary (a value-EQUAL assignment
    /// never fires the hook at all, so re-clicking the primary's own sidebar row while it's a
    /// group's primary needs this explicit path to force the collapse -- see
    /// <see cref="OnElementRowPointerPressed"/>).</summary>
    public void SetSelection(IEnumerable<ITemplateElementViewModel> elements)
    {
        // ToList() FIRST, unconditionally -- a caller (ToggleElementSelection's removal branch, the
        // RemoveOverlayElement/BakeElementIntoBackgroundAsync shrink) may pass a deferred Where() over
        // SelectedOverlayElements itself, which this method then clears; evaluating lazily would
        // observe its own mutation mid-enumeration.
        var newSet = elements.ToList();
        var previous = _updatingSelectionSet;
        _updatingSelectionSet = true;
        try
        {
            SelectedOverlayElements.Clear();
            foreach (var element in newSet)
            {
                SelectedOverlayElements.Add(element);
            }

            SelectedOverlayElement = newSet.Count > 0 ? newSet[^1] : null;
        }
        finally
        {
            _updatingSelectionSet = previous;
        }

        // The assignment above fires OnSelectedOverlayElementChanged, which already re-syncs
        // IsSelected against SelectedOverlayElements (populated before that assignment ran) for
        // every REAL change -- but CommunityToolkit's generated setter skips the hook entirely on a
        // value-EQUAL assignment (e.g. newSet's last element already WAS the primary, the routine
        // case for a group shrink that doesn't touch the primary). Re-run unconditionally here so
        // IsSelected always ends up correct regardless of whether the hook fired.
        foreach (var element in OverlayElements)
        {
            element.IsSelected = SelectedOverlayElements.Contains(element);
        }
    }

    /// <summary>TX editor gap-items plan, item 2 -- shift-click's own selection-toggle gesture:
    /// adds <paramref name="element"/> to the multi-selection set if absent, removes it if present.</summary>
    public void ToggleElementSelection(ITemplateElementViewModel element)
    {
        var current = SelectedOverlayElements.ToList();
        SetSelection(current.Contains(element) ? current.Where(e => !ReferenceEquals(e, element)) : current.Append(element));
    }

    private bool CanInsertField() => SelectedOverlayElement is OverlayElementViewModel;

    /// <summary>Backs the overlay editor's "Insert field" chips -- appends a macro token (e.g.
    /// <c>%m</c>, <c>{grid}</c>) to the currently selected TEXT element's raw
    /// <see cref="OverlayElementViewModel.Text"/>. Gated by <see cref="CanInsertField"/> (Phase 1
    /// plan-review finding: with a box element selected, appending to nothing was a silent no-op --
    /// disabling the chips is better UX than a click that visibly does nothing) rather than a no-op
    /// body -- see <see cref="OnSelectedOverlayElementChanged"/> for the CanExecute re-evaluation
    /// hook.</summary>
    [RelayCommand(CanExecute = nameof(CanInsertField))]
    private void InsertField(string? token)
    {
        if (string.IsNullOrEmpty(token) || SelectedOverlayElement is not OverlayElementViewModel element)
        {
            return;
        }

        element.Text += token;
    }

    /// <summary>Backlog item (user request, 2026-08-17) -- context-menu quick-pick for
    /// <see cref="OverlayElementViewModel.FontSizeRelative"/>, same gate/shape as
    /// <see cref="InsertField"/> (text-only, <see cref="CanInsertField"/>). Unrecognized/null keys
    /// are a no-op (matches <see cref="SetTextColorPreset"/>'s own default-arm behavior) rather than
    /// throwing, since <c>CommandParameter</c> is caller-supplied XAML, not internal state.
    /// No explicit <see cref="PushUndoSnapshotCoalesced"/> call needed here (TX workflow
    /// modernization plan, Phase 1): the assignment below now goes through
    /// <see cref="OverlayElementViewModel.OnFontSizeRelativeChanging"/>, which pushes on its own --
    /// this used to be a real gap (no hook existed at all), closed once the Quick Style Flyout made
    /// this property a primary editing surface, not just a preset shortcut.</summary>
    [RelayCommand(CanExecute = nameof(CanInsertField))]
    private void SetFontSizePreset(string? sizeKey)
    {
        if (SelectedOverlayElement is not OverlayElementViewModel element)
        {
            return;
        }

        element.FontSizeRelative = sizeKey switch
        {
            "Small" => 0.06,
            "Medium" => 0.10,
            "Large" => 0.16,
            "XLarge" => 0.24,
            _ => element.FontSizeRelative,
        };
    }

    /// <summary>Backlog item (user request, 2026-08-17) -- context-menu quick-pick for
    /// <see cref="OverlayElementViewModel.Color"/>, same gate/shape/undo-consistency reasoning as
    /// <see cref="SetFontSizePreset"/>. A curated 6-swatch set (white/black/yellow/red/green/blue),
    /// not the full spectrum -- the TEXT STYLE tab's own <c>ColorPicker</c> stays the full-control
    /// path.</summary>
    [RelayCommand(CanExecute = nameof(CanInsertField))]
    private void SetTextColorPreset(string? colorKey)
    {
        if (SelectedOverlayElement is not OverlayElementViewModel element)
        {
            return;
        }

        element.Color = colorKey switch
        {
            "White" => new Rgb24(255, 255, 255),
            "Black" => new Rgb24(0, 0, 0),
            "Yellow" => new Rgb24(234, 179, 8),
            "Red" => new Rgb24(220, 38, 38),
            "Green" => new Rgb24(34, 197, 94),
            "Blue" => new Rgb24(59, 130, 246),
            _ => element.Color,
        };
    }

    partial void OnSelectedOverlayElementChanged(ITemplateElementViewModel? value)
    {
        // TX editor gap-items plan, item 2 (group-ops-lite, 2026-09-02) -- this hook is the SOLE
        // sync point between SelectedOverlayElement (the single "primary," unchanged in shape) and
        // SelectedOverlayElements (the new multi-selection set): a raw assignment (every existing
        // call site in this file, and every existing test that writes this property directly)
        // always means "select exactly this one" intent, so it collapses the set to match --
        // UNLESS this write came from SetSelection itself (guarded by _updatingSelectionSet, so a
        // real multi-element SetSelection([a, b, c]) doesn't get collapsed by its own trailing
        // primary-assignment). This is a STRUCTURAL guarantee, not a convention every call site has
        // to remember -- but only for a VALUE-CHANGING assignment: CommunityToolkit's generated
        // setter skips this hook entirely when the new value reference-equals the current one (no
        // Equals override on any element VM, confirmed before relying on it), so re-selecting an
        // ALREADY-primary element (e.g. clicking its own sidebar row while it's a 2+ group's
        // primary) does NOT re-fire this collapse -- call sites that need to force a collapse even
        // onto the current primary must go through the public SetSelection/SelectOnly path instead
        // of a raw assignment (see OnElementRowPointerPressed).
        if (!_updatingSelectionSet)
        {
            SelectedOverlayElements.Clear();
            if (value is not null)
            {
                SelectedOverlayElements.Add(value);
            }
        }

        // Backlog item (auditor usability review, 2026-08-17): ELEMENTS panel row highlight -- a
        // plain loop over every live element, same "parent pushes shared state down" convention as
        // every command on ITemplateElementViewModel, just a settable property instead of a command
        // (see IsSelected's own doc comment). O(n) in element count, which this editor's whole
        // undo/redo snapshot mechanism already treats as small/cheap (CaptureSnapshot copies the
        // full element list on every edit). Generalized (TX editor gap-items plan, item 2) from
        // "is this the one SelectedOverlayElement" to "is this a member of SelectedOverlayElements"
        // -- identical result whenever the set has 0-1 members (every pre-existing scenario).
        foreach (var element in OverlayElements)
        {
            element.IsSelected = SelectedOverlayElements.Contains(element);
        }

        if (value is not null)
        {
            SwitchToApplicableTabIfNeeded(value);
        }

        InsertFieldCommand.NotifyCanExecuteChanged();
        SetFontSizePresetCommand.NotifyCanExecuteChanged();
        SetTextColorPresetCommand.NotifyCanExecuteChanged();
        AddPlateBehindTextCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        CopySelectedElementCommand.NotifyCanExecuteChanged();
        CutSelectedElementCommand.NotifyCanExecuteChanged();
        CopySelectedElementStyleCommand.NotifyCanExecuteChanged();
        PasteSelectedElementStyleCommand.NotifyCanExecuteChanged();
        // Element rotation (2026-09-20, code-review finding) -- CanRotateSelectedElement depends on
        // SelectedOverlayElement, same as DuplicateCommand/CopySelectedElementCommand above; without
        // this, these 3 would render stale-disabled on any future entry point that isn't a
        // freshly-opened context menu (Avalonia re-queries CanExecute on popup attach today, which
        // is why this had no visible symptom yet).
        RotateClockwise90Command.NotifyCanExecuteChanged();
        RotateCounterclockwise90Command.NotifyCanExecuteChanged();
        Rotate180Command.NotifyCanExecuteChanged();
        // TX editor gap-items plan, item 4b -- same "gated on a text element being selected" shape
        // as InsertFieldCommand above, needs the same refresh.
        PickTextBitmapFillFromFileCommand.NotifyCanExecuteChanged();
        PickTextBitmapFillFromClipboardCommand.NotifyCanExecuteChanged();
        ClearTextBitmapFillCommand.NotifyCanExecuteChanged();
        // Code-review finding: FontFamilyPickerItems/IsFontUnavailable MUST raise BEFORE
        // SelectedTextElement -- the Font ComboBox's ItemsSource is bound to
        // FontFamilyPickerItems and its SelectedItem (two-way) to SelectedTextElement.FontFamily.
        // Avalonia re-evaluates SelectedItem the moment SelectedTextElement's own change
        // notification fires; if ItemsSource is still the OLD list at that instant (an unavailable
        // font not yet included), SelectedItem resolves to no match and the two-way binding can
        // write that back, silently clobbering the very font name IsFontUnavailable exists to warn
        // about -- exactly the scenario this feature was built for (selecting a text element whose
        // font isn't bundled). Raising the ItemsSource-affecting properties first guarantees the
        // superset list (which always includes the current font, see FontFamilyPickerItems' own doc
        // comment) is already in place before SelectedItem gets re-evaluated.
        OnPropertyChanged(nameof(IsFontUnavailable));
        OnPropertyChanged(nameof(FontFamilyPickerItems));
        OnPropertyChanged(nameof(SelectedTextElement));
        OnPropertyChanged(nameof(SelectedImageElement));
        OnPropertyChanged(nameof(SelectedBoxElement));
        // Code-review finding: the same "check every sibling on a notification set" bug class this
        // project has hit repeatedly -- SelectedLineElement was added alongside SelectedBoxElement's
        // own declaration but never wired into this raise list, leaving the GEOMETRY tab's future
        // line-style block permanently stale on selection change.
        OnPropertyChanged(nameof(SelectedLineElement));
        OnPropertyChanged(nameof(SelectionReadoutText));
        OnPropertyChanged(nameof(SelectedTextElementFontSizePx));
        OnPropertyChanged(nameof(SelectedTextElementStrokeThicknessPx));
        OnPropertyChanged(nameof(SelectedTextElementShadowOffsetXPx));
        OnPropertyChanged(nameof(SelectedTextElementShadowOffsetYPx));
        OnPropertyChanged(nameof(SelectedTextElementStackStepXPx));
        OnPropertyChanged(nameof(SelectedTextElementStackStepYPx));
        OnPropertyChanged(nameof(SelectedBoxElementBorderThicknessPx));
        OnPropertyChanged(nameof(SelectedBoxElementCornerRadiusPx));
        OnPropertyChanged(nameof(SelectedElementLeftPx));
        OnPropertyChanged(nameof(SelectedElementTopPx));
        OnPropertyChanged(nameof(SelectedElementWidthPx));
        OnPropertyChanged(nameof(SelectedElementHeightPx));
        OnPropertyChanged(nameof(IsPerspectiveEligibleElementSelected));
        OnPropertyChanged(nameof(SelectedElementPerspectiveEnabled));
    }

    /// <summary>Phase 4 (spec/15-template-designer.md) -- <see cref="SelectedOverlayElement"/>
    /// narrowed to the TEXT case, or null when a box/image element (or nothing) is selected. Lets
    /// the TEXT STYLE panel's AXAML bind its whole content against this as a nested DataContext
    /// (font family/size/fill/stroke are all TEXT-only concepts) instead of every individual control
    /// needing its own <c>SelectedOverlayElement as OverlayElementViewModel</c> cast -- same
    /// "narrow once, bind against the narrowed type" shape <see cref="CanInsertField"/> already
    /// established for the insert-field chips.</summary>
    public OverlayElementViewModel? SelectedTextElement => SelectedOverlayElement as OverlayElementViewModel;

    /// <summary>Backlog item (user request, 2026-08-17): "text size should be in px not 0.1 or 0.16
    /// etc" -- the TEXT STYLE tab's SIZE field now shows/edits a real pixel count instead of the raw
    /// relative-to-height fraction, while <see cref="OverlayElementViewModel.FontSizeRelative"/>
    /// itself stays the underlying storage (deliberately mode-portable -- see its own doc comment: a
    /// template saved at one SSTV mode must still render correctly at another, so a plain rename to
    /// px storage would break that guarantee). Converts against the TARGET mode's own height, the
    /// same real-pixel convention <c>TransmitImagePreparer.DrawTemplateText</c>'s own
    /// <c>strokeThicknessPx = strokeThicknessRelative * imageHeightPx</c> already uses -- this is the
    /// size the text renders at in the TRANSMITTED image, not canvas-display pixels at the current
    /// zoom (<see cref="OverlayElementViewModel.CanvasFontSize"/> already covers that separate
    /// concern). Get/set both no-op safely when nothing is selected -- a bound TextBox still
    /// round-trips through this property even while its row is hidden by
    /// <c>SelectedTextElement</c>'s own null-check.</summary>
    public double SelectedTextElementFontSizePx
    {
        get => SelectedTextElement is { } text ? text.FontSizeRelative * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.FontSizeRelative = value / _targetMode.ImageHeight;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Stroke thickness / shadow
    /// offsets in TEXT STYLE are still raw relative fractions ... outline width shows '0.004' with no
    /// unit" -- same target-mode-height px conversion as <see cref="SelectedTextElementFontSizePx"/>,
    /// same reasoning (the size this actually renders at in the TRANSMITTED image, zoom-independent).</summary>
    public double SelectedTextElementStrokeThicknessPx
    {
        get => SelectedTextElement is { } text ? text.StrokeThickness * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.StrokeThickness = value / _targetMode.ImageHeight;
        }
    }

    /// <inheritdoc cref="SelectedTextElementStrokeThicknessPx"/>
    public double SelectedTextElementShadowOffsetXPx
    {
        get => SelectedTextElement is { } text ? text.ShadowOffsetX * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.ShadowOffsetX = value / _targetMode.ImageHeight;
        }
    }

    /// <inheritdoc cref="SelectedTextElementStrokeThicknessPx"/>
    public double SelectedTextElementShadowOffsetYPx
    {
        get => SelectedTextElement is { } text ? text.ShadowOffsetY * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.ShadowOffsetY = value / _targetMode.ImageHeight;
        }
    }

    /// <inheritdoc cref="SelectedTextElementStrokeThicknessPx"/>
    public double SelectedTextElementStackStepXPx
    {
        get => SelectedTextElement is { } text ? text.StackStepX * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.StackStepX = value / _targetMode.ImageHeight;
        }
    }

    /// <inheritdoc cref="SelectedTextElementStrokeThicknessPx"/>
    public double SelectedTextElementStackStepYPx
    {
        get => SelectedTextElement is { } text ? text.StackStepY * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedTextElement is not { } text || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            text.StackStepY = value / _targetMode.ImageHeight;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "GEOMETRY X/Y/W/H are raw full-
    /// precision doubles, no px option" -- same conversion shape as <see cref="SelectedTextElementFontSizePx"/>,
    /// generalized to <see cref="SelectedOverlayElement"/> (the shared <see cref="ITemplateElementViewModel"/>
    /// interface, so this works for text/box/image alike, unlike the text-only SIZE field). Converted
    /// against <see cref="WorkingCopyWidth"/>/<see cref="WorkingCopyHeight"/> (zoom-independent native
    /// pixels), NOT <see cref="ITemplateElementViewModel.ImageWidth"/>/<c>ImageHeight</c> (those are
    /// the zoom-premultiplied CANVAS DISPLAY size -- typing a px value would drift with the zoom
    /// slider if this used those instead, the same zoom-independence <see cref="ZoomFactor"/>'s own
    /// doc comment establishes for undo/redo/snap/the pipeline). Left/Top (not center X/Y) since a
    /// px-based GEOMETRY field is most useful matching <see cref="ITemplateElementViewModel.LeftPixels"/>/
    /// <c>TopPixels</c>'s own top-left convention -- <see cref="ITemplateElementViewModel.X"/>/<c>Y</c>
    /// themselves stay center-anchored underneath (unchanged, see that interface's own doc comment),
    /// this is a display/edit-side conversion only, same as every other *Px property here.</summary>
    public double SelectedElementLeftPx
    {
        get => SelectedOverlayElement is { } element ? (element.X - (element.Width / 2)) * WorkingCopyWidth : 0;
        set
        {
            if (SelectedOverlayElement is not { } element || WorkingCopyWidth <= 0)
            {
                return;
            }

            element.X = (value / WorkingCopyWidth) + (element.Width / 2);
        }
    }

    /// <inheritdoc cref="SelectedElementLeftPx"/>
    public double SelectedElementTopPx
    {
        get => SelectedOverlayElement is { } element ? (element.Y - (element.Height / 2)) * WorkingCopyHeight : 0;
        set
        {
            if (SelectedOverlayElement is not { } element || WorkingCopyHeight <= 0)
            {
                return;
            }

            element.Y = (value / WorkingCopyHeight) + (element.Height / 2);
        }
    }

    /// <inheritdoc cref="SelectedElementLeftPx"/>
    public double SelectedElementWidthPx
    {
        get => SelectedOverlayElement is { } element ? element.Width * WorkingCopyWidth : 0;
        set
        {
            if (SelectedOverlayElement is not { } element || WorkingCopyWidth <= 0)
            {
                return;
            }

            element.Width = value / WorkingCopyWidth;
        }
    }

    /// <inheritdoc cref="SelectedElementLeftPx"/>
    public double SelectedElementHeightPx
    {
        get => SelectedOverlayElement is { } element ? element.Height * WorkingCopyHeight : 0;
        set
        {
            if (SelectedOverlayElement is not { } element || WorkingCopyHeight <= 0)
            {
                return;
            }

            element.Height = value / WorkingCopyHeight;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Image elements' Fit mode isn't
    /// editable" -- same narrowed-cast shape as <see cref="SelectedTextElement"/>, for the GEOMETRY
    /// tab's new image-only Fit row.</summary>
    public ImageElementViewModel? SelectedImageElement => SelectedOverlayElement as ImageElementViewModel;

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Box elements have no style UI
    /// at all" -- same narrowed-cast shape as <see cref="SelectedTextElement"/>/<see cref="SelectedImageElement"/>,
    /// for the GEOMETRY tab's new box-only style block.</summary>
    public BoxElementViewModel? SelectedBoxElement => SelectedOverlayElement as BoxElementViewModel;

    /// <summary>TX editor gap-items plan (2026-09-01) -- same narrowed-cast shape as
    /// <see cref="SelectedBoxElement"/>, for the GEOMETRY tab's new line-only style block.</summary>
    public LineElementViewModel? SelectedLineElement => SelectedOverlayElement as LineElementViewModel;

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- true when the currently
    /// selected element is an image or box AND supports perspective (both do). Backs the GEOMETRY
    /// tab's Perspective checkbox's own <c>IsVisible</c> -- one shared row rather than duplicating it
    /// inside BOTH the box-style and image-style blocks above.</summary>
    public bool IsPerspectiveEligibleElementSelected => SelectedOverlayElement is ImageElementViewModel or BoxElementViewModel;

    /// <summary>TX editor gap-items plan, item 3 -- read-only reflection of the selected element's
    /// own <c>PerspectiveEnabled</c> (not part of the shared <see cref="ITemplateElementViewModel"/>
    /// interface, so this narrows by type same as <see cref="SelectedBoxElement"/> above). The
    /// checkbox's own AXAML binds this ONE-WAY for its checked state and separately binds
    /// <see cref="ImageElementViewModel.TogglePerspectiveCommand"/> for the actual click action --
    /// NOT a plain two-way <c>IsChecked</c> binding, because enabling has a real side effect (seeding
    /// the 4 corners from the current bbox) only the command performs correctly; a bare two-way
    /// binding would flip the flag with no seed step. Re-raised from
    /// <see cref="OnSelectedOverlayElementChanged"/> (selection change) and
    /// <see cref="OnOverlayElementPropertyChanged"/> (the selected element's own flag flips).</summary>
    public bool SelectedElementPerspectiveEnabled => SelectedOverlayElement is PerspectiveElementViewModelBase { PerspectiveEnabled: true };

    /// <summary>Same target-mode-height px conversion as <see cref="SelectedTextElementStrokeThicknessPx"/>,
    /// for <see cref="BoxElementViewModel.BorderThickness"/> (same relative-to-image-height convention,
    /// see that property's own doc comment).</summary>
    public double SelectedBoxElementBorderThicknessPx
    {
        get => SelectedBoxElement is { } box ? box.BorderThickness * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedBoxElement is not { } box || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            box.BorderThickness = value / _targetMode.ImageHeight;
        }
    }

    /// <summary>Auditor usability review follow-up (2026-08-18): same target-mode-height px
    /// conversion as <see cref="SelectedBoxElementBorderThicknessPx"/>, for
    /// <see cref="BoxElementViewModel.CornerRadius"/>.</summary>
    public double SelectedBoxElementCornerRadiusPx
    {
        get => SelectedBoxElement is { } box ? box.CornerRadius * _targetMode.ImageHeight : 0;
        set
        {
            if (SelectedBoxElement is not { } box || _targetMode.ImageHeight <= 0)
            {
                return;
            }

            box.CornerRadius = value / _targetMode.ImageHeight;
        }
    }

    /// <summary>Phase 4 -- the TEXT STYLE panel's font-family picker ItemsSource. Forwards
    /// <see cref="ITransmitImagePreparer.AvailableFontFamilies"/> rather than the VM hardcoding its
    /// own copy of what's bundled.</summary>
    public IReadOnlyList<string> AvailableFontFamilies => _preparer.AvailableFontFamilies;

    /// <summary>Phase 6 (spec/15-template-designer.md) -- true when the selected text element's own
    /// <see cref="OverlayElementViewModel.FontFamily"/> isn't one of <see cref="AvailableFontFamilies"/>
    /// (a cross-platform-shared or hand-edited template referencing a font this build doesn't bundle
    /// -- <see cref="ITransmitImagePreparer.ResolveFontFamily"/>'s own real pipeline behavior is to
    /// silently substitute the default font, with nothing today telling the operator that happened).
    /// Lives on the PARENT VM, not per-element -- <see cref="AvailableFontFamilies"/> itself already
    /// lives here, and the whole TEXT STYLE panel already binds <see cref="SelectedTextElement"/>
    /// paths off the parent (plan-review finding: pushing this onto every text element VM would be a
    /// new pattern, not the existing one).</summary>
    public bool IsFontUnavailable => SelectedTextElement is { } text && !AvailableFontFamilies.Contains(text.FontFamily);

    /// <summary>Phase 6 -- the Font `ComboBox`'s own `ItemsSource` (NOT <see cref="AvailableFontFamilies"/>
    /// directly). <see cref="ComboBox.SelectedItem"/> is two-way bound to
    /// <see cref="SelectedTextElement"/>'s own <c>FontFamily</c> -- if the bound value isn't present
    /// in `ItemsSource` at all, Avalonia resolves `SelectedItem` to no match, and a two-way binding
    /// can then write that resolved (non-)value straight back through, silently overwriting the
    /// element's real (if unavailable) font name before the operator ever sees the warning glyph
    /// (plan-review risk). Fix: always include the CURRENTLY selected font name in the list, even if
    /// it's not one of the real bundled families -- guarantees `SelectedItem` always has a match, so
    /// this binding can never silently mutate the underlying value regardless of the exact
    /// no-match resolution behavior. Deliberately a SEPARATE list from <see cref="AvailableFontFamilies"/>:
    /// <see cref="IsFontUnavailable"/>'s own check needs the real bundled set, not this
    /// display-safe superset.</summary>
    public IReadOnlyList<string> FontFamilyPickerItems =>
        SelectedTextElement is { } text && !AvailableFontFamilies.Contains(text.FontFamily)
            ? [.. AvailableFontFamilies, text.FontFamily]
            : AvailableFontFamilies;

    [RelayCommand]
    private void SelectTextStyleTab()
    {
        IsTextStyleTabSelected = true;
        IsGeometryTabSelected = false;
        IsImageTabSelected = false;
    }

    [RelayCommand]
    private void SelectGeometryTab()
    {
        IsTextStyleTabSelected = false;
        IsGeometryTabSelected = true;
        IsImageTabSelected = false;
    }

    [RelayCommand]
    private void SelectImageTab()
    {
        IsTextStyleTabSelected = false;
        IsGeometryTabSelected = false;
        IsImageTabSelected = true;
    }

    /// <summary>TX workflow modernization plan, Phase 2 -- called on plain selection change (NOT on
    /// new-element creation, which force-selects unconditionally instead, see each creation
    /// command's own call site). Geometry applies to every element type, so it's never switched away
    /// from here -- preserves the original "don't fight a user comparing positions" rationale
    /// (<see cref="IsTextStyleTabSelected"/>'s own doc comment) while fixing the common case: an
    /// inapplicable tab (Image while a text element is newly selected, or vice versa) switches to
    /// the one that actually applies. Box elements have no dedicated tab of their own -- their
    /// style lives in Geometry alongside position/size -- so Geometry is also their applicable tab.</summary>
    private void SwitchToApplicableTabIfNeeded(ITemplateElementViewModel element)
    {
        if (IsGeometryTabSelected)
        {
            return;
        }

        switch (element)
        {
            case OverlayElementViewModel when !IsTextStyleTabSelected:
                SelectTextStyleTab();
                break;
            case ImageElementViewModel when !IsImageTabSelected:
                SelectImageTab();
                break;
            case BoxElementViewModel:
            case LineElementViewModel:
                SelectGeometryTab();
                break;
        }
    }

    /// <summary>EditWindow redesign Phase 3, GEOMETRY tab -- aligns the selected element to one edge/
    /// center of the crop rect. <see cref="CropRect"/> is TOP-LEFT-anchored (<c>NormalizedRect.X/Y</c>
    /// is the left/top edge, confirmed via <see cref="CropLeftPixels"/>'s own
    /// <c>CropRect.X * CanvasDisplayWidth</c> formula) but element X/Y are CENTER-anchored
    /// (<see cref="ITemplateElementViewModel"/>'s own doc comment) -- the two conventions differ, so
    /// "align left" is <c>CropRect.X + element.Width / 2</c>, not a bare <c>CropRect.X</c> copy.
    /// A single undo step via <see cref="PushUndoSnapshot"/> -- Tier B audit finding: an earlier
    /// version of this doc comment claimed assigning "exactly one property per call" was enough to
    /// avoid a second push, but each element type's own <c>OnXChanging</c>/<c>OnYChanging</c> hook
    /// calls <see cref="ITemplateElementViewModel.PushUndoSnapshotForGeometryChange"/> regardless of
    /// property count -- <c>_suspendPreview</c> is what actually blocks the second push, same
    /// pattern <see cref="SetAsBackdrop"/> already established.</summary>
    [RelayCommand]
    private void AlignSelectedElementToCrop(string alignment)
    {
        if (SelectedOverlayElement is not { } element)
        {
            return;
        }

        PushUndoSnapshot();
        // Tier B audit finding: the X/Y assignments below used to run unguarded -- every element
        // type's own OnXChanging/OnYChanging hook calls PushUndoSnapshotForGeometryChange, which
        // isn't suppressed or coalesced against the PushUndoSnapshot() just above (that call's own
        // job is clearing _pendingCoalesceProperty, not preventing a second push), so a single Align
        // click pushed TWO undo steps for what visibly is one action -- the first Undo silently did
        // nothing, only the second actually moved the element back. _suspendPreview (same pattern
        // SetAsBackdrop/ApplySnappedElementBounds already use) blocks the per-property hook from
        // pushing its own step.
        _suspendPreview = true;
        try
        {
            switch (alignment)
            {
                case "Left":
                    element.X = CropRect.X + (element.Width / 2);
                    break;
                case "Center":
                    element.X = CropRect.X + (CropRect.Width / 2);
                    break;
                case "Right":
                    element.X = CropRect.X + CropRect.Width - (element.Width / 2);
                    break;
                case "Top":
                    element.Y = CropRect.Y + (element.Height / 2);
                    break;
                case "Middle":
                    element.Y = CropRect.Y + (CropRect.Height / 2);
                    break;
                case "Bottom":
                    element.Y = CropRect.Y + CropRect.Height - (element.Height / 2);
                    break;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    /// <summary>TX workflow modernization plan, Phase 1 "Fit ▸" image-menu submenu -- same
    /// selection-implicit/string-parameter shape as <see cref="AlignSelectedElementToCrop"/> right
    /// above. <see cref="ImageElementViewModel.Fit"/> is a plain <c>[ObservableProperty]</c> with no
    /// geometry-coalesce hook of its own (unlike X/Y/Width/Height), so no <c>_suspendPreview</c>
    /// wrapping is needed here -- a single assignment is already a single undo step.</summary>
    [RelayCommand]
    private void SetSelectedImageFit(string mode)
    {
        if (SelectedOverlayElement is not ImageElementViewModel element || !Enum.TryParse<ImageFitMode>(mode, out var fit))
        {
            return;
        }

        PushUndoSnapshot();
        element.Fit = fit;
        RecomputePreview();
    }

    /// <summary>User-requested (2026-09-15): the safe-area guide's own normalized size, in the SAME
    /// canvas-normalized coordinate space <see cref="CropRect"/> and every element's own X/Y/Width/
    /// Height already live in (confirmed via <see cref="NudgeSelectedElements"/>'s own
    /// <c>dxPixels / WorkingCopyWidth</c> math and <see cref="AlignSelectedElementToCrop"/>'s own
    /// <c>CropRect.X + ...</c> formulas -- both only make sense if CropRect and element geometry share
    /// one space). <see cref="SafeAreaInsetPixels"/>/<see cref="SafeAreaWidthPixels"/> are the SAME
    /// computation in on-screen pixels; dividing by <see cref="WorkingCopyWidth"/>/
    /// <see cref="WorkingCopyHeight"/> instead of <see cref="CanvasDisplayWidth"/>/
    /// <see cref="CanvasDisplayHeight"/> cancels <see cref="ZoomFactor"/> out algebraically, same
    /// zoom-invariance reasoning <see cref="ProjectRectToCropRelative"/>'s own doc comment gives.
    /// Returns <see langword="false"/> for a degenerate result (working copy narrower/shorter than
    /// twice the inset) rather than a negative/zero size -- same guard
    /// <see cref="SafeAreaWidthPixels"/>'s own <c>Math.Max(0, ...)</c> represents in pixel space.</summary>
    private bool TryGetSafeAreaNormalizedSize(out double width, out double height)
    {
        width = height = 0;
        if (WorkingCopyWidth <= 0 || WorkingCopyHeight <= 0)
        {
            return false;
        }

        width = 1 - (2 * SafeAreaInsetWorkingCopyUnits / WorkingCopyWidth);
        height = 1 - (2 * SafeAreaInsetWorkingCopyUnits / WorkingCopyHeight);
        return width > 0 && height > 0;
    }

    private bool CanFitSelectedImageToSafeArea(string target) =>
        SelectedOverlayElement is ImageElementViewModel { Locked: false };

    /// <summary>User-requested (2026-09-15): "right click menu 'fit'... should have an option of
    /// 'fit safe area', fit width, fit height. Those options should resize the image area and image
    /// to the safe area size (or width or height)." A DIFFERENT concept from
    /// <see cref="SetSelectedImageFit"/> right above -- that changes how the image's own PIXELS fill
    /// its EXISTING bounds (Stretch/Contain/Cover); this changes the bounds (Width/Height/X/Y)
    /// themselves to match the safe-area guide's own size, centered, same
    /// "X=0.5,Y=0.5 full-frame convention" <see cref="SetAsBackdrop"/> already uses for its own
    /// exact-size assignment (no aspect-preserving scale here either -- <see cref="ImageElementViewModel.Fit"/>,
    /// set independently via the sibling submenu items, already owns how the pixels handle an
    /// arbitrary box). <paramref name="target"/> mirrors exactly which axis/axes the operator asked
    /// for -- "SafeArea" sets both Width/Height (and both X/Y); "Width"/"Height" touch only that one
    /// axis (size AND its own centering), leaving the other axis' current position/size untouched, a
    /// literal reading of "the safe area size (or width or height)."
    /// <para>Same Locked gate as <see cref="ResetImageElementToOriginalSize"/> -- a locked element's
    /// bounds are frozen by definition (see that command's own doc comment for the exact bug this
    /// prevents), in CanExecute, the body backstop below, and the MenuItem's own AXAML IsEnabled
    /// bind.</para></summary>
    [RelayCommand(CanExecute = nameof(CanFitSelectedImageToSafeArea))]
    private void FitSelectedImageToSafeArea(string target)
    {
        if (SelectedOverlayElement is not ImageElementViewModel { Locked: false } element)
        {
            return;
        }

        if (!TryGetSafeAreaNormalizedSize(out var safeWidth, out var safeHeight))
        {
            StatusMessage = _localization.GetString("Panes.TxImageEditor.FitToSafeAreaUnavailable");
            return;
        }

        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            // Same "turn perspective off first, inside this same suspend window" reasoning as
            // ResetImageElementToOriginalSize/SetAsBackdrop's own identical fix.
            if (element.PerspectiveEnabled)
            {
                element.DisablePerspectiveKeepingBounds();
            }

            switch (target)
            {
                case "SafeArea":
                    element.X = 0.5;
                    element.Y = 0.5;
                    element.Width = safeWidth;
                    element.Height = safeHeight;
                    break;
                case "Width":
                    element.X = 0.5;
                    element.Width = safeWidth;
                    break;
                case "Height":
                    element.Y = 0.5;
                    element.Height = safeHeight;
                    break;
            }
        }
        finally
        {
            _suspendPreview = false;
        }

        RecomputePreview();
    }

    // User-reported bug (2026-09-15): "when i set an image as backdrop and i pick the option 'reset
    // to original size' it gives an error but does not return as a non-backdropped image of the
    // previous size." Root cause: this had no Locked check at all, unlike every other
    // geometry-changing operation in this class (NudgeSelectedElements' own "if (element.Locked)
    // continue" is the established convention) -- a locked backdrop is defined as covering the whole
    // frame (X=0.5,Y=0.5,W=1,H=1, see SetAsBackdrop's own doc comment), so shrinking it to its
    // natural size left it in a broken, self-contradictory state: still Locked and IsBackground=true
    // (so still unreachable by normal drag, still treated as the frame's backdrop) but no longer
    // actually covering the frame. "Gives an error" was the (accurate but confusingly-worded, given
    // what actually happened) ResetToOriginalSizeFitted/ResetToOriginalSizeUnavailable status message
    // -- the real problem is this command running at all on a locked element, not its message text.
    private bool CanResetImageElementToOriginalSize(ImageElementViewModel? element) =>
        element is not null && !element.Locked && element.NaturalPixelWidth > 0 && element.NaturalPixelHeight > 0 && OverlayElements.Contains(element);

    /// <summary>TX workflow modernization plan, Phase 7. "Original size" means the element renders
    /// at its own NATURAL pixel count IN THE TRANSMITTED FRAME, which is not a division by the
    /// mode's dimensions: an element's rendered pixel width is
    /// <c>(Width / CropRect.Width) * contentWidth</c> (<see cref="ProjectRectToCropRelative"/>'s own
    /// <c>finalWidth * targetWidth</c>), so inverting that gives
    /// <c>Width = NaturalPixelWidth * CropRect.Width / contentWidth</c>. <c>contentWidth</c> is the
    /// LETTERBOXED content extent (<see cref="TryGetCropContentMetrics"/>), not
    /// <see cref="_targetMode"/>.ImageWidth -- using the mode dimension instead is wrong by exactly
    /// the pad factor under the default aspect-preserving resize.
    /// <para><b>Observable behavior, stated up front so it doesn't read as broken:</b> inserted
    /// image elements are capped at <see cref="WorkingCopyScaleFactor"/>x the mode's dimensions by
    /// <see cref="DownsampleToBudget"/>, and a real photo's NATURAL size is far larger than that
    /// again -- so for any real photo this lands in the clamp-to-frame branch, and the observable
    /// result is "fit to the crop frame at the image's natural aspect ratio," not a literal 1:1
    /// pixel mapping. The clamp is UNIFORM (one factor on both axes) so the natural aspect always
    /// survives it; the operator is told when it engaged.</para></summary>
    [RelayCommand(CanExecute = nameof(CanResetImageElementToOriginalSize))]
    private void ResetImageElementToOriginalSize(ImageElementViewModel? element)
    {
        // Body-level backstop, same "CanExecute alone isn't a hard gate for a direct Execute() call"
        // reasoning this class's other commands already document (e.g. QuickSelectMode).
        if (element is null || element.Locked || !OverlayElements.Contains(element) || element.NaturalPixelWidth <= 0 || element.NaturalPixelHeight <= 0)
        {
            return;
        }

        if (!TryGetCropContentMetrics(out _, out _, out var contentWidth, out var contentHeight) || contentWidth <= 0 || contentHeight <= 0)
        {
            StatusMessage = _localization.GetString("Panes.TxImageEditor.ResetToOriginalSizeUnavailable");
            return;
        }

        var width = element.NaturalPixelWidth * CropRect.Width / contentWidth;
        var height = element.NaturalPixelHeight * CropRect.Height / contentHeight;

        // Uniform clamp (same factor both axes) if natural size would exceed the crop frame --
        // preserves aspect ratio regardless of how much it engages. Floor matches
        // TxImageEditorPaneView.MinNormalizedElementSize (0.02) -- a separate, deliberately
        // identical constant on that class, not shared, same reasoning as that constant's own doc
        // comment gives for not sharing MinNormalizedCropSize either.
        const double minNormalizedElementSize = 0.02;
        var fit = Math.Min(1.0, Math.Min(CropRect.Width / width, CropRect.Height / height));
        var clamped = fit < 1.0;
        width = Math.Max(width * fit, minNormalizedElementSize);
        height = Math.Max(height * fit, minNormalizedElementSize);

        // One click, one undo step: each assignment below would otherwise push its own coalesced
        // step via OnWidthChanging/OnHeightChanging -- same _suspendPreview pattern
        // AlignSelectedElementToCrop/SetAsBackdrop already use for exactly this.
        PushUndoSnapshot();
        _suspendPreview = true;
        try
        {
            // TX editor gap-items plan, item 3 -- same "turn perspective off first, inside this same
            // suspend window" reasoning as SetAsBackdrop's own identical fix.
            if (element.PerspectiveEnabled)
            {
                element.DisablePerspectiveKeepingBounds();
            }

            element.Width = width;
            element.Height = height;
        }
        finally
        {
            _suspendPreview = false;
        }

        Log.ResetImageElementToOriginalSize(_logger, element.NaturalPixelWidth, element.NaturalPixelHeight, clamped);
        StatusMessage = clamped ? _localization.GetString("Panes.TxImageEditor.ResetToOriginalSizeFitted") : null;
        RecomputePreview();
    }
}
