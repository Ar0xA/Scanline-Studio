using System.IO.Ports;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>
/// `BACKLOG.md` W7, serial half. <c>SerialPortEnumerator</c> passes <c>SerialPort.GetPortNames()</c>
/// straight through, and that yields <c>COM*</c> on Windows against <c>/dev/tty*</c> on Linux — two
/// very different shapes through one code path, with only the Linux shape ever exercised.
///
/// <para><b>Device cost: none, and the boundary is deliberate.</b> <c>GetPortNames()</c> reads names
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
    [WindowsFact]
    public void EnumeratedPortNames_HaveTheWindowsShape()
    {
        // An empty list is a legitimate result -- plenty of machines have no serial ports -- so the
        // assertion is about the shape of what IS returned, never about there being any.
        foreach (var name in SerialPort.GetPortNames())
        {
            Assert.StartsWith("COM", name, StringComparison.Ordinal);

            Assert.True(
                name.Length > 3 && name[3..].All(char.IsDigit),
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
            SerialPort.GetPortNames().OrderBy(n => n, StringComparer.Ordinal),
            SerialPort.GetPortNames().OrderBy(n => n, StringComparer.Ordinal));
    }

    [WindowsFact]
    public void EnumeratedPortNames_ContainNoDuplicates()
    {
        // A duplicate would produce two identical entries in the port dropdown, which reads as a UI
        // bug and would otherwise only be noticed by a user.
        var names = SerialPort.GetPortNames();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }
}
