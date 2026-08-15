namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>Aggregate result of <see cref="IAdifUdpStreamer.SendLoggedQsoAsync"/> -- deliberately
/// no per-destination detail here (that granularity lives in the log, tagged by destination name);
/// callers only need "did forwarding happen and how much of it."</summary>
public sealed record AdifUdpSendResult(int SentCount, int EnabledCount);
