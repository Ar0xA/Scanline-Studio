using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

public sealed partial class OptionsWindowViewModel
{
    /// <summary>Backs the Decode tab's 4-way Sense level radio group -- same computed-bool-property
    /// idiom as <see cref="IsNoneBackendSelected"/>/etc. above. Index order (0=Very low..3=Very high)
    /// matches <c>Option.dfm</c>'s real <c>RGSLvl</c> item order and
    /// <see cref="ScanlineStudio.Core.Sstv.AnalogFmSstvDecoder.SenseLevelPresets"/>.</summary>
    public bool IsSenseLevelVeryLowSelected
    {
        get => SenseLevel == 0;
        set
        {
            if (value)
            {
                SenseLevel = 0;
            }
        }
    }

    public bool IsSenseLevelLowSelected
    {
        get => SenseLevel == 1;
        set
        {
            if (value)
            {
                SenseLevel = 1;
            }
        }
    }

    public bool IsSenseLevelHighSelected
    {
        get => SenseLevel == 2;
        set
        {
            if (value)
            {
                SenseLevel = 2;
            }
        }
    }

    public bool IsSenseLevelVeryHighSelected
    {
        get => SenseLevel == 3;
        set
        {
            if (value)
            {
                SenseLevel = 3;
            }
        }
    }

    /// <summary>Backs the Appearance tab's 3-way theme radio group -- same computed-bool-property
    /// idiom as <see cref="IsSenseLevelVeryLowSelected"/>/etc above.</summary>
    public bool IsAppThemeLightSelected
    {
        get => AppTheme == AppTheme.Light;
        set
        {
            if (value)
            {
                AppTheme = AppTheme.Light;
            }
        }
    }

    public bool IsAppThemeDarkSelected
    {
        get => AppTheme == AppTheme.Dark;
        set
        {
            if (value)
            {
                AppTheme = AppTheme.Dark;
            }
        }
    }

    public bool IsAppThemeSystemSelected
    {
        get => AppTheme == AppTheme.System;
        set
        {
            if (value)
            {
                AppTheme = AppTheme.System;
            }
        }
    }

    /// <summary>Backs the Appearance tab's font-scale radio group -- same computed-bool-property
    /// idiom as <see cref="IsAppThemeLightSelected"/>/etc above.</summary>
    public bool IsFontScaleNormalSelected
    {
        get => FontScale == AppFontScale.Normal;
        set
        {
            if (value)
            {
                FontScale = AppFontScale.Normal;
            }
        }
    }

    public bool IsFontScaleLargeSelected
    {
        get => FontScale == AppFontScale.Large;
        set
        {
            if (value)
            {
                FontScale = AppFontScale.Large;
            }
        }
    }

    /// <summary>Backs the Decode tab's 3-way Demod type radio group -- same computed-bool-property
    /// idiom as <see cref="IsSenseLevelVeryLowSelected"/>/etc above. Item order (0=PLL/1=Zero
    /// crossing/2=Hilbert) matches <c>Option.dfm</c>'s real <c>RGDemType</c> item order and
    /// <see cref="DemodType"/>'s own enum values.</summary>
    public bool IsDemodTypePllSelected
    {
        get => DemodType == DemodType.Pll;
        set
        {
            if (value)
            {
                DemodType = DemodType.Pll;
            }
        }
    }

    public bool IsDemodTypeZeroCrossingSelected
    {
        get => DemodType == DemodType.ZeroCrossing;
        set
        {
            if (value)
            {
                DemodType = DemodType.ZeroCrossing;
            }
        }
    }

    public bool IsDemodTypeHilbertSelected
    {
        get => DemodType == DemodType.Hilbert;
        set
        {
            if (value)
            {
                DemodType = DemodType.Hilbert;
            }
        }
    }

    /// <summary>Backs the Advanced tab's 3-way zero-crossing smoothing-mode radio group (Options stub
    /// backlog item 2) -- same computed-bool-property idiom as <see cref="IsDemodTypePllSelected"/>
    /// above. Item order (0=Iir/1=Fir/2=Off) matches <see cref="ZeroCrossingSmoothingMode"/>'s own
    /// enum values, corroborated (not source-confirmed, see the plan doc) against legacy's real
    /// <c>RGcrossType</c> labels "IIR"/"FIR"/"OFF".</summary>
    public bool IsZeroCrossingSmoothingIirSelected
    {
        get => ZeroCrossingSmoothingMode == ZeroCrossingSmoothingMode.Iir;
        set
        {
            if (value)
            {
                ZeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Iir;
            }
        }
    }

    public bool IsZeroCrossingSmoothingFirSelected
    {
        get => ZeroCrossingSmoothingMode == ZeroCrossingSmoothingMode.Fir;
        set
        {
            if (value)
            {
                ZeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Fir;
            }
        }
    }

    public bool IsZeroCrossingSmoothingOffSelected
    {
        get => ZeroCrossingSmoothingMode == ZeroCrossingSmoothingMode.Off;
        set
        {
            if (value)
            {
                ZeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Off;
            }
        }
    }

    /// <summary>Greys the IIR sub-group (order+cutoff fields) unless IIR smoothing is selected --
    /// legacy's real `GBCOI.Enabled` gate (`Option.cpp:197-212`; this port shows the group always
    /// rather than replicating legacy's own hide/show elsewhere, same precedent as
    /// <see cref="IsDemodTypePllSelected"/>'s own AXAML binding).</summary>
    public bool IsZeroCrossingIirGroupEnabled => IsZeroCrossingSmoothingIirSelected;

    /// <summary>Greys the FIR sub-group (smoothing-frequency field) unless FIR smoothing is selected --
    /// legacy's real `GBCOF.Enabled` gate, same reasoning as <see cref="IsZeroCrossingIirGroupEnabled"/>
    /// above.</summary>
    public bool IsZeroCrossingFirGroupEnabled => IsZeroCrossingSmoothingFirSelected;

    /// <summary>Backs the Decode tab's 4-way RX BPF radio group -- same computed-bool-property idiom
    /// as <see cref="IsDemodTypePllSelected"/>/etc above (N-way-exclusive, not
    /// <see cref="CwIdMode"/>'s 2-plus-disabled shape). Item order (0=Off/1=Wide/2=Narrow/
    /// 3=VeryNarrow) matches <c>Option.dfm</c>'s real <c>RGRxBPF</c> item order and
    /// <see cref="RxBpfPreset"/>'s own enum values. "Off" keeps its existing UI label "Normal" (a
    /// deliberate, prior, already-committed relabeling of legacy's real "OFF" item -- not this
    /// property's own naming choice).</summary>
    public bool IsRxBpfOffSelected
    {
        get => RxBpfPreset == RxBpfPreset.Off;
        set
        {
            if (value)
            {
                RxBpfPreset = RxBpfPreset.Off;
            }
        }
    }

    public bool IsRxBpfWideSelected
    {
        get => RxBpfPreset == RxBpfPreset.Wide;
        set
        {
            if (value)
            {
                RxBpfPreset = RxBpfPreset.Wide;
            }
        }
    }

    public bool IsRxBpfNarrowSelected
    {
        get => RxBpfPreset == RxBpfPreset.Narrow;
        set
        {
            if (value)
            {
                RxBpfPreset = RxBpfPreset.Narrow;
            }
        }
    }

    public bool IsRxBpfVeryNarrowSelected
    {
        get => RxBpfPreset == RxBpfPreset.VeryNarrow;
        set
        {
            if (value)
            {
                RxBpfPreset = RxBpfPreset.VeryNarrow;
            }
        }
    }

    /// <summary>Backs the Decode tab's 3-way RX buffering radio group -- same computed-bool-property
    /// idiom as <see cref="IsRxBpfOffSelected"/>/etc above. Item order (0=Off/1=On/2=Extended)
    /// matches <c>Option.dfm</c>'s real <c>RGRBuf</c> item order and <see cref="RxBufferMode"/>'s own
    /// enum values.</summary>
    public bool IsRxBufferOffSelected
    {
        get => RxBufferMode == RxBufferMode.Off;
        set
        {
            if (value)
            {
                RxBufferMode = RxBufferMode.Off;
            }
        }
    }

    public bool IsRxBufferOnSelected
    {
        get => RxBufferMode == RxBufferMode.On;
        set
        {
            if (value)
            {
                RxBufferMode = RxBufferMode.On;
            }
        }
    }

    public bool IsRxBufferExtendedSelected
    {
        get => RxBufferMode == RxBufferMode.Extended;
        set
        {
            if (value)
            {
                RxBufferMode = RxBufferMode.Extended;
            }
        }
    }

    /// <summary>Gates the Decode tab's Auto Slant checkbox row -- legacy's own
    /// <c>CBASlant->Enabled = RGRBuf->ItemIndex ? TRUE : FALSE</c> (`Option.cpp:222`): Auto Slant has
    /// no effect with RX buffering off (no staging buffer to track a correction against), so the
    /// checkbox is disabled, not hidden, matching this dialog's own established per-control disable
    /// convention. Not persisted itself -- purely a UI-enablement derivation from
    /// <see cref="RxBufferMode"/>.</summary>
    public bool IsAutoSlantRowEnabled => RxBufferMode != RxBufferMode.Off;

    /// <summary>Backs the Identification tab's "ID method" radio group -- same computed-bool idiom
    /// as <see cref="IsSenseLevelVeryLowSelected"/>/etc above.</summary>
    public bool IsIdMethodOffSelected
    {
        get => CwIdMode == CwIdMode.Off;
        set
        {
            if (value)
            {
                CwIdMode = CwIdMode.Off;
            }
        }
    }

    public bool IsIdMethodCwSelected
    {
        get => CwIdMode == CwIdMode.Cw;
        set
        {
            if (value)
            {
                CwIdMode = CwIdMode.Cw;
            }
        }
    }

    public bool IsIdMethodSoundFileSelected
    {
        get => CwIdMode == CwIdMode.SoundFile;
        set
        {
            if (value)
            {
                CwIdMode = CwIdMode.SoundFile;
            }
        }
    }

    /// <summary>Backs the Audio tab's 3-way Stereo capture source radio group -- same computed-bool
    /// idiom as <see cref="IsSenseLevelVeryLowSelected"/>/etc above.</summary>
    public bool IsCaptureChannelMonoSelected
    {
        get => CaptureChannelSource == AudioChannelSource.Mono;
        set
        {
            if (value)
            {
                CaptureChannelSource = AudioChannelSource.Mono;
            }
        }
    }

    public bool IsCaptureChannelLeftSelected
    {
        get => CaptureChannelSource == AudioChannelSource.Left;
        set
        {
            if (value)
            {
                CaptureChannelSource = AudioChannelSource.Left;
            }
        }
    }

    public bool IsCaptureChannelRightSelected
    {
        get => CaptureChannelSource == AudioChannelSource.Right;
        set
        {
            if (value)
            {
                CaptureChannelSource = AudioChannelSource.Right;
            }
        }
    }

    partial void OnSenseLevelChanged(int value)
    {
        OnPropertyChanged(nameof(IsSenseLevelVeryLowSelected));
        OnPropertyChanged(nameof(IsSenseLevelLowSelected));
        OnPropertyChanged(nameof(IsSenseLevelHighSelected));
        OnPropertyChanged(nameof(IsSenseLevelVeryHighSelected));
    }

    partial void OnAppThemeChanged(AppTheme value)
    {
        OnPropertyChanged(nameof(IsAppThemeLightSelected));
        OnPropertyChanged(nameof(IsAppThemeDarkSelected));
        OnPropertyChanged(nameof(IsAppThemeSystemSelected));
    }

    partial void OnFontScaleChanged(AppFontScale value)
    {
        OnPropertyChanged(nameof(IsFontScaleNormalSelected));
        OnPropertyChanged(nameof(IsFontScaleLargeSelected));
    }

    partial void OnDemodTypeChanged(DemodType value)
    {
        OnPropertyChanged(nameof(IsDemodTypePllSelected));
        OnPropertyChanged(nameof(IsDemodTypeZeroCrossingSelected));
        OnPropertyChanged(nameof(IsDemodTypeHilbertSelected));
    }

    partial void OnZeroCrossingSmoothingModeChanged(ZeroCrossingSmoothingMode value)
    {
        OnPropertyChanged(nameof(IsZeroCrossingSmoothingIirSelected));
        OnPropertyChanged(nameof(IsZeroCrossingSmoothingFirSelected));
        OnPropertyChanged(nameof(IsZeroCrossingSmoothingOffSelected));
        OnPropertyChanged(nameof(IsZeroCrossingIirGroupEnabled));
        OnPropertyChanged(nameof(IsZeroCrossingFirGroupEnabled));
    }

    partial void OnRxBpfPresetChanged(RxBpfPreset value)
    {
        OnPropertyChanged(nameof(IsRxBpfOffSelected));
        OnPropertyChanged(nameof(IsRxBpfWideSelected));
        OnPropertyChanged(nameof(IsRxBpfNarrowSelected));
        OnPropertyChanged(nameof(IsRxBpfVeryNarrowSelected));
    }

    partial void OnRxBufferModeChanged(RxBufferMode value)
    {
        OnPropertyChanged(nameof(IsRxBufferOffSelected));
        OnPropertyChanged(nameof(IsRxBufferOnSelected));
        OnPropertyChanged(nameof(IsRxBufferExtendedSelected));
        OnPropertyChanged(nameof(IsAutoSlantRowEnabled));
    }

    partial void OnCaptureChannelSourceChanged(AudioChannelSource value)
    {
        OnPropertyChanged(nameof(IsCaptureChannelMonoSelected));
        OnPropertyChanged(nameof(IsCaptureChannelLeftSelected));
        OnPropertyChanged(nameof(IsCaptureChannelRightSelected));
    }
}
