using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>One row in the Phase 3 fill bar (spec/15-template-designer.md, "named template
/// variables + fill bar") -- one per template variable name (e.g. <c>his_call</c>) currently
/// referenced by at least one live text element's raw <c>Text</c>.
/// <para>ROW VISIBILITY is derived by <see cref="TxImageEditorPaneViewModel"/>'s own rescan (a row
/// is added when a key is first referenced, removed when no live element references it anymore),
/// but the underlying VALUE lives in that VM's own persistent <c>Dictionary&lt;string,string&gt;</c>,
/// not on this row -- editing an existing <c>{his_call}</c> token character-by-character passes
/// through syntactically-valid intermediate tokens (<c>{his_cal}</c>, <c>{his_ca}</c>, ...) on
/// every keystroke, and a naive "the row disappeared, so drop the value" design would silently
/// destroy the operator's already-typed callsign on the very first backspace. This row's own
/// <see cref="Value"/> is only ever the CURRENT reflection of that persistent value, seeded at
/// construction and pushed back out via <see cref="ValueChangedCallback"/> on every edit.</para></summary>
public sealed partial class TemplateVariableRowViewModel : ObservableObject
{
    public string Key { get; }

    [ObservableProperty]
    private string _value;

    /// <summary>Parent-pushed (same pattern as <see cref="ITemplateElementViewModel.RemoveCommand"/>)
    /// -- invoked with <c>(Key, Value)</c> whenever <see cref="Value"/> changes, so
    /// <see cref="TxImageEditorPaneViewModel"/> can update its own persistent dictionary and
    /// explicitly trigger the recompute/font-refresh/canvas-binding-refresh chain that a bare
    /// property-changed raise on this row alone would not reach (see that VM's own doc comment on
    /// its template-variable-changed handler for why). Settable (not <c>init</c>) -- code-review
    /// finding: <see cref="ResetDisplayValueWithoutNotifying"/> needs to suppress it temporarily,
    /// which an <c>init</c>-only property can't support post-construction.</summary>
    public Action<string, string>? ValueChangedCallback { get; set; }

    public TemplateVariableRowViewModel(string key, string value)
    {
        Key = key;
        _value = value;
    }

    partial void OnValueChanged(string value) => ValueChangedCallback?.Invoke(Key, value);

    /// <summary>Code-review finding: <see cref="Value"/>'s own setter unconditionally invokes
    /// <see cref="ValueChangedCallback"/> via <see cref="OnValueChanged"/> -- there is no way to
    /// update the DISPLAYED value through that setter without also re-writing the parent VM's
    /// persistent dictionary. <c>ClearTemplateVariables</c> needs exactly that: blank every row's
    /// display WITHOUT re-seeding the dictionary it just cleared (setting <c>row.Value =
    /// string.Empty</c> there would immediately call back and write `""` right back into the
    /// dictionary for every key, defeating the clear). Goes through the ordinary generated
    /// <see cref="Value"/> setter (so <c>PropertyChanged</c> still fires correctly for the
    /// canvas/fill-bar binding -- writing the <c>[ObservableProperty]</c> backing field directly is
    /// a hard analyzer error, MVVMTK0034, in this codebase's build), just with
    /// <see cref="ValueChangedCallback"/> temporarily suppressed and restored in a
    /// <see langword="finally"/> block (UI-thread-only VM, no reentrancy concern here).</summary>
    internal void ResetDisplayValueWithoutNotifying(string value)
    {
        var callback = ValueChangedCallback;
        ValueChangedCallback = null;
        try
        {
            Value = value;
        }
        finally
        {
            ValueChangedCallback = callback;
        }
    }
}
