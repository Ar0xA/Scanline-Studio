using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using Microsoft.Extensions.Time.Testing;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;

namespace ScanlineStudio.UI.Tests;

public sealed class UnhandledUiExceptionHandlerTests
{
    private sealed class FakeNotice : IUnexpectedErrorNotice
    {
        private TaskCompletionSource? _open;

        public int ShowCount { get; private set; }

        public bool ThrowOnShow { get; set; }

        public Task ShowAsync()
        {
            ShowCount++;
            if (ThrowOnShow)
            {
                throw new InvalidOperationException("notice failed");
            }

            _open = new TaskCompletionSource();
            return _open.Task;
        }

        public void Close() => _open?.TrySetResult();
    }

    private static (UnhandledUiExceptionHandler Handler, FakeNotice Notice, FakeTimeProvider Clock, FakeLogger<UnhandledUiExceptionHandler> Logger) Create()
    {
        var notice = new FakeNotice();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));
        var logger = new FakeLogger<UnhandledUiExceptionHandler>();
        return (new UnhandledUiExceptionHandler(logger, notice, clock), notice, clock, logger);
    }

    [AvaloniaFact]
    public void Installed_APostedJobThrows_IsHandledLoggedAndNoticed_AndAfterDisposeItPropagatesAgain()
    {
        var (handler, notice, _, logger) = Create();
        var subscription = handler.Install(Dispatcher.UIThread);
        try
        {
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("boom"));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, notice.ShowCount);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
        }
        finally
        {
            subscription.Dispose();
        }

        // Unsubscribed: a later test's dispatcher exceptions must not be silently swallowed by a leaked handler.
        Dispatcher.UIThread.Post(() => throw new InvalidOperationException("after dispose"));
        Assert.ThrowsAny<Exception>(() => Dispatcher.UIThread.RunJobs());
    }

    [AvaloniaFact]
    public void Dialog_LoadsAndItsOkCommandClosesIt()
    {
        App.Services = new ServiceCollection()
            .AddSingleton<ILocalizationService>(new FakeLocalizationService())
            .BuildServiceProvider();
        var viewModel = new UnexpectedErrorDialogViewModel();
        var view = new UnexpectedErrorDialogView { DataContext = viewModel };
        var closed = false;
        view.Closed += (_, _) => closed = true;
        view.Show();
        Dispatcher.UIThread.RunJobs();

        viewModel.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
    }

    [AvaloniaFact]
    public void Install_Twice_SubscribesOnce()
    {
        var (handler, _, _, logger) = Create();
        using var first = handler.Install(Dispatcher.UIThread);
        using var second = handler.Install(Dispatcher.UIThread);

        Dispatcher.UIThread.Post(() => throw new InvalidOperationException("once"));
        Dispatcher.UIThread.RunJobs();

        Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [AvaloniaFact]
    public void SeveralExceptionsInOneBatch_ScheduleOneNotice()
    {
        var (handler, notice, _, _) = Create();

        handler.Handle(new InvalidOperationException("1"));
        handler.Handle(new InvalidOperationException("2"));
        handler.Handle(new InvalidOperationException("3"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, notice.ShowCount);
    }

    [AvaloniaFact]
    public void WhileTheNoticeIsOpen_NoSecondNotice_AfterCloseOnlyOnceTheCooldownHasPassed()
    {
        var (handler, notice, clock, _) = Create();
        handler.Handle(new InvalidOperationException("first"));
        Dispatcher.UIThread.RunJobs();

        handler.Handle(new InvalidOperationException("while open"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, notice.ShowCount);

        notice.Close();
        Dispatcher.UIThread.RunJobs();
        clock.Advance(UnhandledUiExceptionHandler.NoticeCooldown - TimeSpan.FromSeconds(1));
        handler.Handle(new InvalidOperationException("inside cooldown"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, notice.ShowCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        handler.Handle(new InvalidOperationException("after cooldown"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, notice.ShowCount);
    }

    [AvaloniaFact]
    public void ANoticeThatThrows_DoesNotEscape_AndIsLogged()
    {
        var (handler, notice, clock, logger) = Create();
        notice.ThrowOnShow = true;

        handler.Handle(new InvalidOperationException("first"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("notice failed", StringComparison.OrdinalIgnoreCase));
        clock.Advance(UnhandledUiExceptionHandler.NoticeCooldown);
        notice.ThrowOnShow = false;
        handler.Handle(new InvalidOperationException("second"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, notice.ShowCount);
    }

    [AvaloniaFact]
    public void AFlood_LogsFullDetailUpToTheLimit_ThenOneLineEach_AndResetsAfterTheWindow()
    {
        var (handler, _, clock, logger) = Create();
        var limit = UnhandledUiExceptionHandler.FullDetailLimitPerWindow;

        for (var i = 0; i < limit + 3; i++)
        {
            handler.Handle(new InvalidOperationException($"flood {i}"));
        }

        Assert.Equal(limit, logger.Entries.Count(e => e.Message.StartsWith("Unhandled exception on the UI thread;", StringComparison.Ordinal)));
        Assert.Equal(3, logger.Entries.Count(e => e.Message.Contains("stack omitted", StringComparison.Ordinal)));

        clock.Advance(UnhandledUiExceptionHandler.FloodWindow);
        handler.Handle(new InvalidOperationException("next minute"));
        Assert.Equal(limit + 1, logger.Entries.Count(e => e.Message.StartsWith("Unhandled exception on the UI thread;", StringComparison.Ordinal)));
    }
}
