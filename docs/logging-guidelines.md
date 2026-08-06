# Logging guidelines

Every new function/method that does I/O, calls an external system (radio, audio device, file,
settings store), or handles a user-initiated command must include appropriate logging. This is a
standing requirement, not optional polish — see `CLAUDE.md`'s pointer to this file. Established
2026-08-06 after an unlogged UI click silently failed to invoke its bound command with no trace
anywhere to diagnose it from.

## Infrastructure

- `Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder` (already used in
  `ScanlineStudio.Host/Program.cs`) wires a default console `ILoggerProvider` plus DI-resolvable
  `ILogger<T>` everywhere. A second provider, `ScanlineStudio.Host/FileLoggerProvider.cs`, appends
  to a fixed path (`%LocalAppData%/ScanlineStudio/logs/app.log` on Windows, the XDG-equivalent
  elsewhere), overwritten fresh each launch — read this file directly when debugging; don't rely
  on console capture alone.
- Any project that needs `ILogger<T>` must reference the `Microsoft.Extensions.Logging.Abstractions`
  package (`Version="8.0.2"`, matching every other package pin in this repo) — most projects don't
  have it yet; add it the first time a class in that project needs a logger.

## The `[LoggerMessage]` pattern — mandatory, not a style preference

This project has `TreatWarningsAsErrors` + Roslyn analyzers enabled. **CA1848 makes a plain
`logger.LogDebug("...")` call a build error.** Every log call site must use the source-generated
`[LoggerMessage]` pattern instead, in a nested `private static partial class Log` inside the
calling type:

```csharp
public sealed partial class SomeService  // the containing type must be `partial`
{
    private readonly ILogger<SomeService> _logger;

    public void DoThing(string id)
    {
        Log.ThingStarted(_logger, id);
        try { ... }
        catch (Exception ex)
        {
            Log.ThingFailed(_logger, id, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Thing started: {Id}")]
        public static partial void ThingStarted(ILogger logger, string id);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Thing failed: {Id}")]
        public static partial void ThingFailed(ILogger logger, string id, Exception ex);
    }
}
```

Reference implementation: `ScanlineStudio.Core.Localization/JsonLocalizationService.cs`'s own
`Log` class (predates this guideline, already followed the pattern). If the containing type isn't
already `partial`, make it `partial` — this is a mechanical, safe change (never changes runtime
behavior on its own).

## Level guide

- **Critical** — the process is about to die or already has (unhandled exception handlers,
  `AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException`).
- **Error** — an operation failed in a way the user will notice or that loses data/state (a
  received image failed to save, a settings write failed, an unhandled exception in a user
  command). Always include the `Exception` parameter when one exists.
- **Warning** — a handled failure that degrades behavior but the app recovers from cleanly (radio
  auto-connect failed at startup, a single meter read failed, a thumbnail failed to load). Also
  used for safety-relevant events even when nothing "failed" (an SWR auto-cutoff firing).
- **Information** — a significant lifecycle/state-transition event a human skimming the log would
  want to see even with verbosity turned down: radio connected/disconnected, RX/TX started/stopped,
  settings saved, culture changed. One line per event, not a play-by-play.
- **Debug** — user-initiated command entry points and detailed flow useful when actively
  investigating something (a slider debounce firing, a favorite mode selected). This is the
  default minimum level while active development/debugging is ongoing (see below) — expect it to
  be verbose but not overwhelming.
- **Trace** — reserved for genuinely per-iteration/hot-path detail (e.g. `RadioController`'s
  per-poll state publish, typically ~1/sec). Rare; justify explicitly if you reach for it.

## Hot-path rule — non-negotiable

**Never log unconditionally from the audio drain thread, the radio poll loop, or any per-sample/
per-line DSP callback.** `FileLogger` does a synchronous, lock-serialized disk write per call —
a log line on a real-time audio callback becomes dropped RX samples, not just a slow log. If a hot
path needs failure visibility, log the *first* occurrence at full level, then track a counter and
log only a periodic summary (see `MiniAudioCaptureSession`'s existing
`_subscriberExceptionCount`/`_lastSubscriberException` fields for the established pattern) — or log
only a state *transition* (e.g. radio connected → disconnected), never every poll/sample.

## Current minimum level

`Program.cs` sets `LogLevel.Debug` as the global floor while this project is in active
development/debugging. This is deliberately verbose and not the intended shipped default — flip to
`Information` (via the same `SetMinimumLevel` call, or the runtime toggle once built) at the first
real release tag.
