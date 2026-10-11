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
    // [A-Za-z0-9_]+, case-sensitive, no spaces -- same grammar as MacroTextResolver's own
    // BraceTokenPattern (Phase 3 plan-review's decided token grammar), duplicated here (not shared)
    // since this scan serves a different purpose (discovering which KEYS to show a fill-bar row
    // for, not resolving VALUES) and lives in a different project/layer than the resolver.
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex TemplateVariableTokenPattern();

    /// <summary>EditWindow redesign Phase 4 (mockups/Editwindow) -- the bottom QSO FILL bar's own
    /// "no fields yet" placeholder. A PLAIN computed property (not <c>[ObservableProperty]</c>,
    /// since there's no backing field to observe) needs an explicit <see cref="OnPropertyChanged"/>
    /// raise wherever <see cref="TemplateVariableRows"/> actually changes -- <see
    /// cref="RescanTemplateVariables"/> is the one place that adds/removes rows, so it's the one
    /// place this needs raising. Not bound as a bare <c>!TemplateVariableRows.Count</c> path in AXAML
    /// -- Avalonia's binding NOT-operator is for booleans, not a safe implicit int-to-bool
    /// conversion, so a real bool property is the correct fix here, not a shortcut.</summary>
    public bool HasNoTemplateVariableRows => TemplateVariableRows.Count == 0;

    /// <summary>Design-fidelity Phase F (#9, mockups/Editwindow line 239) -- SEND side's compact
    /// mode/duration meta line. The mock's own version also shows SWR, which this VM has no access
    /// to (a TX-hardware meter reading that lives on <c>TxControlsPaneViewModel</c>, cross-VM data
    /// this pass doesn't wire up) -- a real, deliberate scope cut, not silently dropped. Mode name
    /// uppercased for DISPLAY only (Phase I, final confirming pass nit) -- SSTV mode display names
    /// are fixed Latin protocol abbreviations, never user data, so <c>ToUpperInvariant</c> is a safe
    /// display-side transform, not a rename of <see cref="SstvModeDefinition.DisplayName"/> itself.</summary>
    public string SendMetaText => _localization.GetString(
        "Panes.TxImageEditor.SendMetaFormat", _targetMode.DisplayName.ToUpperInvariant(), TxControlsPaneViewModel.GetFrameSeconds(_targetMode));

    /// <summary>Snapshot of the current template-variable value map, for a host
    /// (<see cref="TxControlsPaneViewModel"/>) to capture alongside <see cref="RawOverlayElements"/>
    /// when building its own re-open <c>EditState</c> -- same "read-only snapshot for callers that
    /// shouldn't hold a live reference into this VM's own mutable state" reasoning as
    /// <see cref="RawOverlayElements"/> itself. A defensive copy (not the live
    /// <see cref="_templateVariables"/> dictionary itself) so a caller that squirrels this away
    /// (e.g. into an undo/redo <c>EditorSnapshot</c>) isn't silently mutated by later edits.</summary>
    public IReadOnlyDictionary<string, string> TemplateVariables => new Dictionary<string, string>(_templateVariables);

    /// <summary>Phase 3 (spec/15-template-designer.md) -- discovers which template-variable KEYS are
    /// currently referenced by scanning every live text element's RAW <c>Text</c> (plan-review fix:
    /// NOT <see cref="Document"/>/<c>ResolvedText</c>, which are already-resolved by construction and
    /// would find nothing or only unfilled leftovers). Called from <see cref="RecomputePreview"/>, so
    /// it runs on every mutation that could add/remove a reference (element add/remove/Text edit,
    /// Undo/Redo). Only adjusts <see cref="TemplateVariableRows"/> (row VISIBILITY) -- NEVER writes
    /// into <see cref="_templateVariables"/> itself (real-window finding: an eager empty-string seed
    /// on first discovery made an unfilled variable resolve to "" instead of verbatim, since
    /// `MacroTextResolver`'s own unfilled-resolves-verbatim branch only triggers when the key is
    /// ABSENT from the dictionary -- see this method's own inline comment). An already-typed value
    /// survives a key's temporary de-reference for the same reason it was never written speculatively
    /// in the first place (see <see cref="_templateVariables"/>'s own doc comment for the exact
    /// character-by-character-editing scenario this protects against).</summary>
    private void RescanTemplateVariables()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in OverlayElements.OfType<OverlayElementViewModel>())
        {
            foreach (Match match in TemplateVariableTokenPattern().Matches(element.Text))
            {
                var token = match.Groups[1].Value;
                if (!KnownMacroTokenNames.Contains(token))
                {
                    referenced.Add(token);
                }
                else if (token is "dist" or "bearing")
                {
                    // Tier B audit follow-up: {dist}/{bearing} resolve FROM the "his_grid" variable
                    // (MacroTextResolver.TryResolveDistanceBearing), not from a literal {his_grid}
                    // token -- without this, a template referencing ONLY {dist}/{bearing} (the DIST/
                    // BEARING chips' own real output) never got a fill-bar row at all, so the
                    // operator had no way to type HIS grid in and both tokens resolved to "" forever.
                    referenced.Add("his_grid");
                }
            }
        }

        for (var i = TemplateVariableRows.Count - 1; i >= 0; i--)
        {
            if (!referenced.Contains(TemplateVariableRows[i].Key))
            {
                TemplateVariableRows.RemoveAt(i);
            }
        }

        var existingKeys = new HashSet<string>(TemplateVariableRows.Select(r => r.Key), StringComparer.Ordinal);
        foreach (var key in referenced)
        {
            if (existingKeys.Contains(key))
            {
                continue;
            }

            // Real-window finding (not caught by MacroTextResolverTests' own isolated unit test,
            // which never exercises this seeding path): do NOT write into _templateVariables here.
            // An eager `_templateVariables[key] = string.Empty` on first discovery would make the
            // dictionary key ALWAYS exist the instant a token is typed -- MacroTextResolver's own
            // "unfilled resolves verbatim" branch only triggers when the key is ABSENT, so an eager
            // seed makes that branch practically unreachable (every token resolves to "" the moment
            // it's typed, before the operator ever gets a chance to fill it in). Read-only lookup:
            // display whatever's already there (a value typed before this key's LAST
            // de-reference), or empty for a genuinely untouched key -- the dictionary itself is
            // written to ONLY by an actual edit (OnTemplateVariableValueChanged) or Clear.
            _templateVariables.TryGetValue(key, out var value);

            // User-reported (2026-09-20): "rsv" ({rsv}, the "HIS RSV" chip -- Panes.TxImageEditor.Chip9)
            // is the one fill-bar variable with a genuinely sensible non-blank starting value: SSTV
            // operators near-universally report "595" to the other station regardless of actual
            // picture quality (OperatorSettings.DefaultRst's own doc comment), so it's a near-certain
            // retype otherwise. Seeded with a REAL value (not the empty-string seed the comment above
            // rules out), so it resolves immediately in the live preview instead of showing "{rsv}"
            // verbatim -- still exactly as editable as every other row afterward
            // (OnTemplateVariableValueChanged below overwrites this the moment the operator types a
            // different value for THIS specific QSO). Every other key (his_call/his_grid/anything
            // else) has no sensible non-blank default and keeps the plain "blank until typed"
            // behavior this method's own doc comment already establishes -- only checked when `value`
            // is still null (this key's first-ever discovery), so an operator who deliberately cleared
            // it back to blank on an earlier pass is never silently re-seeded.
            if (key == "rsv" && value is null)
            {
                value = string.IsNullOrWhiteSpace(_operatorSettings.DefaultRst) ? OperatorSettings.DefaultRstFallback : _operatorSettings.DefaultRst;
                _templateVariables[key] = value;

                // User-reported (2026-09-20): without this, the CANVAS stayed showing "{rsv}"
                // verbatim even though the baked preview (which re-resolves fresh from
                // _templateVariables on every RecomputePreview call, further down this same method's
                // caller) already showed the seeded value -- OverlayElementViewModel.ResolvedText is
                // a plain computed getter, so the canvas TextBlock only re-reads it in response to an
                // explicit PropertyChanged(ResolvedText) raise. The element's own OnTextChanged
                // already fired that raise once, synchronously, the instant Text was set to include
                // "{rsv}" -- but that happened BEFORE this method (called from the same Text-changed
                // chain, via RecomputePreview) had written the seed into _templateVariables, so it
                // captured the old, unseeded resolution. Same "explicitly re-raise after the
                // resolution CONTEXT changes" shape as OnTemplateVariableValueChanged below.
                foreach (var seededElement in OverlayElements.OfType<OverlayElementViewModel>())
                {
                    if (seededElement.Text.Contains("{rsv}", StringComparison.Ordinal))
                    {
                        seededElement.NotifyResolvedTextChanged();
                    }
                }
            }

            TemplateVariableRows.Add(new TemplateVariableRowViewModel(key, value ?? string.Empty)
            {
                ValueChangedCallback = OnTemplateVariableValueChanged,
            });
        }

        OnPropertyChanged(nameof(HasNoTemplateVariableRows));
    }

    /// <summary>Fired by a <see cref="TemplateVariableRowViewModel"/>'s own
    /// <see cref="TemplateVariableRowViewModel.ValueChangedCallback"/> whenever the operator types
    /// into a fill-bar row. Plan-review blocker: does NOT rely on
    /// <see cref="OverlayElementViewModel.ResolvedText"/>'s own property-changed raise alone --
    /// <see cref="OnOverlayElementPropertyChanged"/> explicitly filters <c>ResolvedText</c> out of
    /// its own recompute trigger (treated as a derived/computed property, same tier as
    /// <c>CanvasFontSize</c>), and that filtering held safe pre-Phase-3 only because
    /// <c>ResolvedText</c> never changed independently of its owning element's own <c>Text</c> --
    /// this handler is the first place that breaks that coincidence (a fill-bar edit changes what
    /// MANY elements resolve to, without any of their own <c>Text</c> changing), so it explicitly
    /// drives every step <see cref="OnOverlayElementPropertyChanged"/> would otherwise have chained
    /// together: the canvas <c>TextBlock</c> binding (via
    /// <see cref="OverlayElementViewModel.NotifyResolvedTextChanged"/>, scoped to only the elements
    /// that actually reference this key), the font-shrink-to-fit recompute, and the real pipeline
    /// preview -- mirroring how <see cref="Rotate"/>'s own multi-element loop already drives both
    /// explicitly rather than trusting a property-changed cascade to add up to the same effect.</summary>
    private void OnTemplateVariableValueChanged(string key, string value)
    {
        // Tier B audit finding: this used to store an empty string unconditionally -- MacroTextResolver
        // only renders a token VERBATIM (e.g. "{his_call}") when its key is ABSENT from
        // _templateVariables; a PRESENT-but-empty key resolves to "", so blanking a fill-bar field
        // used to render "DE " instead of "DE {his_call}" -- the exact "DE " state
        // ClearTemplateVariablesCommand's own doc comment already classifies as a code-review
        // blocker for the bulk-clear path (easy to transmit by mistake). Two UI paths to the same
        // intent (clear one field vs. clear all) must produce the same result.
        if (value.Length == 0)
        {
            _templateVariables.Remove(key);
        }
        else
        {
            _templateVariables[key] = value;
        }
        // Moved here from RescanTemplateVariables (real-window finding, see that method's own
        // comment) -- this is now the ONLY place _templateVariables gains a new key, so it's the
        // only place that can flip CanClearTemplateVariables from false to true.
        ClearTemplateVariablesCommand.NotifyCanExecuteChanged();
        var token = $"{{{key}}}";
        // Tier B audit finding: {dist}/{bearing} resolve FROM the "his_grid" variable
        // (MacroTextResolver.TryResolveDistanceBearing), not from a literal {his_grid} token in the
        // element's own Text -- an element reading e.g. "DIST {dist}" contains no "{his_grid}"
        // substring at all, so the plain Contains(token) check below never matched it, leaving the
        // canvas TextBlock showing a stale distance/bearing after a his_grid fill-bar edit even
        // though RefreshOverlayElementCanvasStyles()/RecomputePreview() below both read
        // ResolvedText fresh and updated correctly -- a canvas-vs-preview divergence.
        var alsoRefreshDistanceBearing = key == "his_grid";
        foreach (var element in OverlayElements.OfType<OverlayElementViewModel>())
        {
            if (element.Text.Contains(token, StringComparison.Ordinal)
                || (alsoRefreshDistanceBearing && (element.Text.Contains("{dist}", StringComparison.Ordinal) || element.Text.Contains("{bearing}", StringComparison.Ordinal))))
            {
                element.NotifyResolvedTextChanged();
            }
        }

        RefreshOverlayElementCanvasStyles();
        RecomputePreview();
    }

    private bool CanClearTemplateVariables() => _templateVariables.Count > 0;

    /// <summary>The one real, spec-backed fill-bar action (spec/15-template-designer.md: "one
    /// 'clear fields' action after the QSO") -- removes every persisted variable KEY entirely
    /// (visible row or not; a key that's currently hidden because nothing references it right now
    /// still belongs to "that QSO's data" and must not survive to the next one), not just blank its
    /// value. <para>[Code-review blocker, fixed here] The original draft set each value to
    /// <c>string.Empty</c> instead of removing the key -- <see cref="MacroTextResolver"/>'s own
    /// unfilled-resolves-VERBATIM branch only fires when a key is ABSENT from the dictionary (see
    /// <see cref="_templateVariables"/>'s own doc comment), so a present-but-empty value made every
    /// `{token}` silently resolve to nothing instead of showing the token again -- exactly the
    /// "content vanishes instead of showing an obvious placeholder" failure this phase's own
    /// verbatim-resolution decision exists to prevent (a `DE {his_call}` reading `DE ` with no
    /// callsign, easy to transmit by mistake). A real `.Clear()`, not a per-key blank, also fixes
    /// <see cref="CanClearTemplateVariables"/> being permanently stuck true (it checks
    /// `Count > 0`, which a per-key blank never changes).</para>
    /// <para>Row display values are reset via <see cref="TemplateVariableRowViewModel
    /// .ResetDisplayValueWithoutNotifying"/>, NOT the ordinary <c>Value</c> setter -- that setter
    /// invokes <see cref="TemplateVariableRowViewModel.ValueChangedCallback"/> (wired to
    /// <see cref="OnTemplateVariableValueChanged"/>), which would immediately re-write an empty
    /// string right back into the dictionary this method just cleared, silently undoing the fix
    /// above.</para>
    /// <para>Iterates a defensive <c>.ToList()</c> snapshot of <see cref="TemplateVariableRows"/>
    /// (code-review risk, fixed here) -- <see cref="RecomputePreview"/> below re-enters
    /// <see cref="RescanTemplateVariables"/>, which can Add/Remove on that same collection; today
    /// nothing in this method changes which keys are referenced, so it's not yet reachable, but
    /// iterating the live collection directly is one future behavior change away from
    /// <c>InvalidOperationException</c> (the sibling dictionary-key loop above already defends the
    /// same way).</para> "Fill all" is deliberately NOT implemented (Phase 3 plan-review decision)
    /// -- no real default-value source exists yet (<see cref="OperatorSettings"/> is MY-side only;
    /// spec/15 frames any logbook-based prefill as future/additive, not this phase).</summary>
    [RelayCommand(CanExecute = nameof(CanClearTemplateVariables))]
    private void ClearTemplateVariables()
    {
        if (_templateVariables.Count == 0)
        {
            return;
        }

        PushUndoSnapshot();
        _templateVariables.Clear();
        ClearTemplateVariablesCommand.NotifyCanExecuteChanged();

        foreach (var row in TemplateVariableRows.ToList())
        {
            row.ResetDisplayValueWithoutNotifying(string.Empty);
        }

        foreach (var element in OverlayElements.OfType<OverlayElementViewModel>())
        {
            element.NotifyResolvedTextChanged();
        }

        RefreshOverlayElementCanvasStyles();
        RecomputePreview();
    }

    /// <summary>Macros help plan (2026-09-01), item B -- opens the same Macros reference window
    /// `Tools ▸ Macros` does, from the QSO FILL bar header, so an operator mid-template-edit doesn't
    /// have to remember a completely different menu exists. See <see cref="_macrosReferenceRequested"/>'s
    /// own doc comment for the unwired-in-tests contract.</summary>
    [RelayCommand]
    private void OpenMacrosReference() => _macrosReferenceRequested?.Invoke();
}
