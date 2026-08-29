namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Why <c>ScanlineStudio.Application.ISstvSessionService.ValidateStationIdSoundFileAsync</c>
/// rejected a path -- an enum, not a raw string, so the Application layer never does UI localization
/// (spec/10-localization.md: only the UI layer localizes); the Options dialog maps each value to its
/// own loc key.</summary>
public enum SoundFileIdValidationFailure
{
    /// <summary><see cref="SoundFileIdValidationResult.IsValid"/> was <see langword="true"/> --
    /// this value is never itself a real failure reason.</summary>
    None,

    /// <summary>No file exists at the configured path.</summary>
    FileNotFound,

    /// <summary>The file exceeds the size cap -- see that check's own doc comment in
    /// <c>SstvSessionService</c> for the exact limit.</summary>
    FileTooLarge,

    /// <summary>The file's header isn't a playable <c>.mmv</c> layout
    /// (<c>ScanlineStudio.Core.Sstv.MmvSoundFile.ParseHeader</c>'s own doc comment).</summary>
    UnplayableHeader,

    /// <summary>The path exists but couldn't be read (permissions, an invalid path shape a free-text
    /// TextBox can produce, etc.) -- see the validating method's own doc comment for the exact
    /// exception types this covers.</summary>
    ReadError,
}

/// <summary>Result of validating a configured sound-file station-ID path, independent of actually
/// resolving it for playback (<c>ScanlineStudio.Application.ISstvSessionService.ValidateStationIdSoundFileAsync</c>)
/// -- ui_transition_plan.md step 8 (T2-2). <see cref="DurationSeconds"/> is the file's own ORIGINAL
/// sample rate's duration, not resampled to any particular TX target rate (there may be no live
/// encoder/session to target when the Options dialog validates a freshly-picked path).</summary>
public readonly record struct SoundFileIdValidationResult(bool IsValid, double? DurationSeconds, SoundFileIdValidationFailure Failure)
{
    public static SoundFileIdValidationResult Ok(double durationSeconds) => new(true, durationSeconds, SoundFileIdValidationFailure.None);

    public static SoundFileIdValidationResult Fail(SoundFileIdValidationFailure failure) => new(false, null, failure);
}
