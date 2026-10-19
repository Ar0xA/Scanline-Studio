using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace ScanlineStudio.UI.Services;

/// <summary>Catches exceptions that escape a UI-thread dispatcher job (event handlers, async-void
/// continuations, posted work): logs them, marks them handled so the app keeps running, and shows one
/// <see cref="IUnexpectedErrorNotice"/>. Worker-thread exceptions are not covered; they still reach
/// <c>AppDomain.UnhandledException</c>.
/// <para>Concurrency: every field is UI-thread-only. <see cref="Handle"/> runs from the dispatcher's
/// UnhandledException event, and the posted notice continuation resumes on the UI thread
/// (<c>ConfigureAwait(true)</c>), so no locks are needed.</para>
/// <para>No notice storm: at most one notice is open, and none opens again within
/// <see cref="NoticeCooldown"/> of the last one closing. No log flood: after
/// <see cref="FullDetailLimitPerWindow"/> exceptions within <see cref="FloodWindow"/>, further ones are
/// logged as one line without the stack trace.</para></summary>
public sealed partial class UnhandledUiExceptionHandler
{
    public const int FullDetailLimitPerWindow = 20;
    public static readonly TimeSpan FloodWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan NoticeCooldown = TimeSpan.FromSeconds(60);

    private readonly ILogger<UnhandledUiExceptionHandler> _logger;
    private readonly IUnexpectedErrorNotice _notice;
    private readonly TimeProvider _timeProvider;

    private Dispatcher? _dispatcher;
    private Dispatcher? _installedOn;
    private bool _noticeOpen;
    private DateTimeOffset? _noticeClosedAt;
    private DateTimeOffset _floodWindowStart = DateTimeOffset.MinValue;
    private int _countInFloodWindow;

    public UnhandledUiExceptionHandler(ILogger<UnhandledUiExceptionHandler> logger, IUnexpectedErrorNotice notice, TimeProvider timeProvider)
    {
        _logger = logger;
        _notice = notice;
        _timeProvider = timeProvider;
    }

    /// <summary>Subscribes to <paramref name="dispatcher"/>. Call after Avalonia setup has initialized the UI
    /// dispatcher. Idempotent; disposing the result unsubscribes.</summary>
    public IDisposable Install(Dispatcher dispatcher)
    {
        if (_installedOn is null)
        {
            _dispatcher = dispatcher;
            _installedOn = dispatcher;
            dispatcher.UnhandledException += OnUnhandledException;
        }

        return new Subscription(this);
    }

    /// <summary>Logs <paramref name="exception"/> and schedules the notice if allowed. Never throws.</summary>
    public void Handle(Exception exception)
    {
        try
        {
            LogException(exception);
            ScheduleNoticeIfAllowed();
        }
        catch (Exception secondary)
        {
            // A throw out of an UnhandledException handler would end the app; this is the last line.
            try
            {
                Log.HandlerFailed(_logger, secondary);
            }
            catch (Exception)
            {
                // Logging itself failed; nothing safe is left to do.
            }
        }
    }

    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Avalonia reuses one args instance; read it before anything that could re-enter.
        var exception = e.Exception;
        e.Handled = true;
        Handle(exception);
    }

    private void LogException(Exception exception)
    {
        var now = _timeProvider.GetUtcNow();
        if (now - _floodWindowStart >= FloodWindow)
        {
            _floodWindowStart = now;
            _countInFloodWindow = 0;
        }

        _countInFloodWindow++;
        if (_countInFloodWindow <= FullDetailLimitPerWindow)
        {
            Log.UnhandledUiException(_logger, exception);
        }
        else
        {
            Log.UnhandledUiExceptionRepeated(_logger, exception.GetType().FullName ?? exception.GetType().Name, exception.Message, _countInFloodWindow);
        }
    }

    private void ScheduleNoticeIfAllowed()
    {
        if (_noticeOpen)
        {
            return;
        }

        if (_noticeClosedAt is { } closedAt && _timeProvider.GetUtcNow() - closedAt < NoticeCooldown)
        {
            return;
        }

        // Set before posting, so several exceptions drained in one dispatcher batch schedule one notice.
        _noticeOpen = true;
        try
        {
            (_dispatcher ?? Dispatcher.UIThread).Post(() => _ = ShowNoticeAsync(), DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            _noticeOpen = false;
            Log.NoticeFailed(_logger, ex);
        }
    }

    private async Task ShowNoticeAsync()
    {
        try
        {
            await _notice.ShowAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.NoticeFailed(_logger, ex);
        }
        finally
        {
            _noticeOpen = false;
            _noticeClosedAt = _timeProvider.GetUtcNow();
        }
    }

    private sealed class Subscription(UnhandledUiExceptionHandler owner) : IDisposable
    {
        public void Dispose()
        {
            if (owner._installedOn is { } dispatcher)
            {
                dispatcher.UnhandledException -= owner.OnUnhandledException;
                owner._installedOn = null;
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on the UI thread; caught, the app keeps running")]
        public static partial void UnhandledUiException(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on the UI thread (repeat {Count} in this minute, stack omitted): {Type}: {ExceptionMessage}")]
        public static partial void UnhandledUiExceptionRepeated(ILogger logger, string type, string exceptionMessage, int count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Unexpected-error notice failed")]
        public static partial void NoticeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled-exception handler itself failed")]
        public static partial void HandlerFailed(ILogger logger, Exception ex);
    }
}
