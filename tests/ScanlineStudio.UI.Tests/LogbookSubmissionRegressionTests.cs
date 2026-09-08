using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class LogbookSubmissionRegressionTests
{
    [AvaloniaTheory]
    [InlineData("oops", false)]
    [InlineData("14,230", false)]
    [InlineData("NaN", false)]
    [InlineData("1e20", false)]
    [InlineData("-1", false)]
    [InlineData("oops", true)]
    [InlineData("14,230", true)]
    public async Task InvalidFrequency_RejectsLogAndUpdateWithoutSavingPreviousFrequency(string text, bool update)
    {
        var logbook = new FakeLogbookSessionService();
        var original = new QsoRecord("qso", "N0CALL", DateTimeOffset.UtcNow, null, 14_230_000, null,
            null, null, null, null, null, null, null, null, null, false, false);
        if (update)
        {
            logbook.Records.Add(original);
        }

        var vm = new LogbookPaneViewModel(logbook, new FakeFilePickerService(), new FakeSstvSessionService(),
            new FakeLocalizationService(), new FakeReceiveHistoryStore(), NullLogger<LogbookPaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        if (update)
        {
            vm.SelectedEntry = original;
        }
        vm.FormCallsign = "N0CALL";
        vm.FormFrequencyHz = 14_230_000;
        vm.FormFrequencyMhzText = text;
        vm.FormNotes = "must not be saved";

        await (update ? vm.UpdateCommand : vm.LogCommand).ExecuteAsync(null);

        Assert.Equal("Panes.Logbook.Error.InvalidFrequency", vm.StatusMessage);
        Assert.Equal(text, vm.FormFrequencyMhzText);
        if (update)
        {
            Assert.Equal(original, Assert.Single(logbook.Records));
        }
        else
        {
            Assert.Empty(logbook.Records);
        }

        vm.FormFrequencyMhzText = string.Empty;
        await (update ? vm.UpdateCommand : vm.LogCommand).ExecuteAsync(null);
        Assert.Null(Assert.Single(logbook.Records).FrequencyHz);
    }
}
