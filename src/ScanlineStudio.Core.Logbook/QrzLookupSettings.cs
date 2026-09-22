namespace ScanlineStudio.Core.Logbook;

/// <summary>Persisted QRZ.com XML Callbook lookup configuration — see spec/08-logging.md's "QRZ.com
/// lookup" section and legacy `qrzcom.cpp`. Opt-in, off by default, same posture as
/// <see cref="QrzUploadSettings"/> -- the app never queries QRZ without the user explicitly
/// enabling it and supplying their own account credentials (never legacy's hardcoded personal
/// username/password). Nullable properties only, per the STJ-missing-property-defaults-to-CLR-default
/// trap documented on <see cref="QrzUploadSettings"/>/<see cref="AdifUdpStreamingSettings"/>.
///
/// The password itself lives in the OS credential store when one exists; only
/// <c>ScanlineStudio.Application.QrzCredentialService</c> reads or writes it. <see cref="Password"/> here
/// is the plaintext fallback (no keyring on this machine) and the pre-migration location — never read
/// it directly for a lookup.</summary>
public sealed record QrzLookupSettings
{
    public const string SectionKey = "QrzLookup";

    public bool? Enabled { get; init; }

    public string? Username { get; init; }

    /// <summary>Plaintext fallback / pre-migration value. A non-empty value wins over the credential store.</summary>
    public string? Password { get; init; }

    /// <summary>"Credential present" flag: the password was last written to the OS credential store.
    /// Read-site fallback is <see langword="false"/>. Lets a session without a reachable keyring report
    /// "keyring unavailable" instead of "no password".</summary>
    public bool? PasswordInCredentialStore { get; init; }
}
