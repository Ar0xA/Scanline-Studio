using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Core.Radio.Hamlib;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>
/// `BACKLOG.md` W7, serial half. <c>SerialPortEnumerator</c> passes
/// <c>SerialPort.GetPortNames()</c> straight through, and that yields <c>COM*</c> on Windows against
/// <c>/dev/tty*</c> on Linux — two very different shapes through one code path, with only the Linux
/// shape ever exercised.
///
/// <para><b>Device cost: none, and the boundary is deliberate.</b> <c>SerialPort.GetPortNames()</c> reads names
/// from the registry. It does NOT open a port. Opening one would take it from whatever already holds
/// it — on this project's target machine, plausibly a radio's CAT link mid-QSO. So these tests assert
/// on the SHAPE of the enumerated names and stop there.</para>
///
/// <para><b>What is deliberately NOT tested here.</b> That a port can be opened, that it round-trips
/// bytes, or that a CAT command reaches a radio. Those need real hardware and would interrupt it.
/// They belong to the manual hardware checklist, not to a suite someone runs while operating.</para>
/// </summary>
public sealed class WindowsSerialPortNameTests
{
    // The PRODUCTION type, not SerialPort.GetPortNames() directly -- going straight to the BCL would
    // leave SerialPortEnumerator, the thing this file's doc comment names, still unexecuted on Windows.
    private static IReadOnlyList<string> Enumerate() =>
        new SerialPortEnumerator(NullLogger<SerialPortEnumerator>.Instance).GetPortNames();

    [WindowsFact]
    public void EnumeratedPortNames_HaveTheWindowsShape()
    {
        // An empty list is a legitimate result -- plenty of machines have no serial ports -- so the
        // assertion is about the shape of what IS returned, never about there being any.
        foreach (var name in Enumerate())
        {
            Assert.StartsWith("COM", name, StringComparison.Ordinal);

            Assert.True(
                name.Length > 3 && name[3..].All(char.IsAsciiDigit),
                $"'{name}' begins with COM but its remainder is not a number, so it is not a port "
                + "name the rest of the application can round-trip through settings.");
        }
    }

    [WindowsFact]
    public void EnumeratedPortNames_AreStableAcrossRepeatedCalls()
    {
        // A registry read returning a different set on a second call would make a saved port
        // selection unreliable -- a settings bug rather than a driver one, and invisible to any test
        // that enumerates only once.
        Assert.Equal(
            Enumerate().OrderBy(n => n, StringComparer.Ordinal),
            Enumerate().OrderBy(n => n, StringComparer.Ordinal));
    }

    [WindowsFact]
    public void EnumeratedPortNames_ContainNoDuplicates()
    {
        // A duplicate would produce two identical entries in the port dropdown, which reads as a UI
        // bug and would otherwise only be noticed by a user.
        var names = Enumerate();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }
}
