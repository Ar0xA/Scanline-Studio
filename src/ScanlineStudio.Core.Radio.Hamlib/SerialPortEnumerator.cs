using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>Real <see cref="ISerialPortEnumerator"/> -- a direct passthrough to
/// <see cref="SerialPort.GetPortNames"/>. That call is documented as unsupported on macOS
/// (<see cref="PlatformNotSupportedException"/>) -- caught here (alongside <see cref="IOException"/>/
/// <see cref="UnauthorizedAccessException"/> for a locked-down environment) so a load failure on one
/// platform never crashes the Options dialog's load path; the serial port fields are editable
/// ComboBoxes, so manual entry is always the fallback.</summary>
public sealed partial class SerialPortEnumerator : ISerialPortEnumerator
{
    private readonly ILogger<SerialPortEnumerator> _logger;

    public SerialPortEnumerator(ILogger<SerialPortEnumerator>? logger = null)
    {
        _logger = logger ?? NullLogger<SerialPortEnumerator>.Instance;
    }

    public IReadOnlyList<string> GetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            Log.GetPortNamesFailed(_logger, ex);
            return [];
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Listing serial ports failed on this platform")]
        public static partial void GetPortNamesFailed(ILogger logger, Exception exception);
    }
}
