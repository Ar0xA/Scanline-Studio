using System.Runtime.Versioning;
using ScanlineStudio.Core.Radio.OmniRig;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>
/// `BACKLOG.md` W5. `OmniRigComClient`'s COM declarations are hand-authored from OmniRig's type
/// library header, and until now nothing had ever run them against OmniRig. The existing tests are a
/// fake, a reflection check that the `[Guid]`/`[DispId]` attributes match the source, and mapper unit
/// tests — all three pass equally well if every GUID is wrong, because they compare our declarations
/// against themselves.
///
/// <para><b>These drive the PRODUCTION client, not a copy of it.</b> Every read below goes through
/// `OmniRigComClient` exactly as the application does, so each one exercises a real
/// <c>QueryInterface</c> for our IID and a real dispatch on our DISPID. A wrong CLSID fails to
/// activate; a wrong IID fails the cast inside <c>ConnectAsync</c>; a wrong DISPID fails the
/// individual read. That is the first check of our transcription against OmniRig itself.</para>
///
/// <para><b>Device cost — read before running.</b> Connecting starts OmniRig's COM server. If it is
/// configured with a real rig, that server may OPEN THE SERIAL PORT and begin polling, exactly as if
/// you had launched OmniRig yourself. That is what instantiating OmniRig means and this test cannot
/// prevent it. Opt-in, and not while operating.</para>
///
/// <para><b>Every operation here is a READ.</b> Nothing sets a frequency, a mode or PTT. Those
/// commands change a transmitter's state, and a test suite must never issue them — they belong to
/// the manual hardware checklist. The write-side declarations therefore stay unverified by design,
/// and that is recorded rather than quietly ignored.</para>
///
/// <para><b>Skips rather than fails when OmniRig is absent.</b> It is third-party software this
/// project does not ship, so its absence is not a defect here.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OmniRigRealComObjectTests(ITestOutputHelper output)
{
    [OmniRigInstalledFact]
    public async Task Connect_ActivatesTheRealServerAndBindsOurDeclaredInterfaces()
    {
        // ConnectAsync resolves CLSID_OmniRigX, creates the object, reads Rig1 at DISPID 3 and casts
        // it to IID_IRigX. Four separate transcription facts, any of which would throw here.
        await using var client = new OmniRigComClient();
        await client.ConnectAsync(CancellationToken.None);
    }

    [OmniRigInstalledFact]
    public async Task EveryReadOnlyMember_DispatchesAgainstTheRealServer()
    {
        await using var client = new OmniRigComClient();
        await client.ConnectAsync(CancellationToken.None);

        // Each of these is a distinct DISPID on IRigX. Reading them all is the point: an individually
        // mistyped dispatch id throws only when that one member is touched, so a test that read a
        // single property would leave the rest unverified.
        var status = await client.GetStatusAsync(CancellationToken.None);
        var statusText = await client.GetStatusTextAsync(CancellationToken.None);
        var frequency = await client.GetFrequencyHzAsync(CancellationToken.None);
        var tx = await client.GetTxAsync(CancellationToken.None);
        var mode = await client.GetModeAsync(CancellationToken.None);

        // StatusStr is OmniRig's own human-readable string. It is never empty, whatever the rig state
        // -- "Rig is not responding" and "Port is not available" are both valid values here.
        Assert.False(
            string.IsNullOrWhiteSpace(statusText),
            "StatusStr came back empty, which OmniRig never returns -- DISPID 7 is likely wrong.");

        output.WriteLine($"status={status} ({statusText}) freq={frequency} tx={tx} mode={mode}");
    }

    [OmniRigInstalledFact]
    public async Task WhenARigIsActuallyConnected_TheValuesAreCoherent()
    {
        await using var client = new OmniRigComClient();
        await client.ConnectAsync(CancellationToken.None);

        var status = await client.GetStatusAsync(CancellationToken.None);
        if (status != RigStatusX.Online)
        {
            // Reported, not asserted: whether a rig is connected is a property of the machine. But it
            // must be VISIBLE, so a green run with no rig is not mistaken for coverage of the values.
            output.WriteLine(
                $"NOT COVERED: no rig online (status={status}). Frequency and mode were not checked "
                + "against a real radio. Connect a rig in OmniRig to exercise this.");
            return;
        }

        var frequency = await client.GetFrequencyHzAsync(CancellationToken.None);
        var mode = await client.GetModeAsync(CancellationToken.None);

        // Freq is VT_I4 on the wire, and the declaration deliberately does not widen it. A rig tuned
        // above about 2.1 GHz would overflow, which no amateur HF/VHF/UHF rig OmniRig drives does --
        // so a value outside a sane RF range means the marshalling is wrong, not the radio.
        Assert.InRange(frequency, 100_000, 1_300_000_000);

        Assert.True(
            Enum.IsDefined(mode),
            $"mode came back as {(int)mode}, which is not a defined RigParamX value -- DISPID 18 "
            + "may be returning something other than the mode word.");

        output.WriteLine($"COVERED against a live rig: freq={frequency} Hz, mode={mode}");
    }

    [OmniRigInstalledFact]
    public async Task ConnectingTwice_DoesNotLeakOrThrow()
    {
        // Two clients at once is a real case: the application can be reconfigured while a previous
        // backend is still shutting down. COM apartment and reference-counting mistakes surface here
        // rather than in the single-client path.
        await using (var first = new OmniRigComClient())
        {
            await first.ConnectAsync(CancellationToken.None);

            await using var second = new OmniRigComClient();
            await second.ConnectAsync(CancellationToken.None);

            Assert.False(string.IsNullOrWhiteSpace(await second.GetStatusTextAsync(CancellationToken.None)));
        }

        // And once more after both are disposed, to prove disposal released rather than broke it.
        await using var third = new OmniRigComClient();
        await third.ConnectAsync(CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(await third.GetStatusTextAsync(CancellationToken.None)));
    }
}

/// <summary>
/// Windows, opt-in, AND OmniRig actually registered. Three conditions, because each absence means
/// something different: not Windows is not applicable, not opted in is "this can open a serial port",
/// and not registered is "third-party software this project does not ship is not installed".
/// </summary>
public sealed class OmniRigInstalledFactAttribute : FactAttribute
{
    public const string OptInVariable = "SCANLINE_OMNIRIG_COM";

    private static readonly Guid ClsidOmniRigX = new("0839E8C6-ED30-4950-8087-966F970F0CAE");

    public OmniRigInstalledFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: OmniRig is a Windows COM server.";
            return;
        }

        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Skip = $"Opt-in: set {OptInVariable}=1. Connecting starts OmniRig's server, which may "
                + "open the configured rig's serial port and begin polling.";
            return;
        }

        if (OperatingSystem.IsWindows() && Type.GetTypeFromCLSID(ClsidOmniRigX) is null)
        {
            Skip = "OmniRig is not registered on this machine. It is third-party software this "
                + "project does not ship, so its absence is not a defect here.";
        }
    }
}
