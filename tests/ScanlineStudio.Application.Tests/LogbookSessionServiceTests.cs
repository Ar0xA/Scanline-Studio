using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

public sealed class LogbookSessionServiceTests
{
    private static QsoRecord SampleRecord(string id = "1") =>
        new(id, "N0CALL", DateTimeOffset.UtcNow, null, null, null, "martin1", null, null, null, null, null, null, null, null);

    private static LogbookSessionService CreateService(
        FakeLogbookRepository? repository = null,
        FakeGridTrackerStreamer? gridTrackerStreamer = null,
        FakeQrzLogbookUploader? qrzUploader = null,
        FakeSettingsStore? settingsStore = null)
    {
        return new LogbookSessionService(
            repository ?? new FakeLogbookRepository(),
            new AdifExporter(),
            new AdifImporter(),
            gridTrackerStreamer ?? new FakeGridTrackerStreamer(),
            qrzUploader ?? new FakeQrzLogbookUploader(),
            settingsStore ?? new FakeSettingsStore(),
            NullLogger<LogbookSessionService>.Instance);
    }

    [Fact]
    public async Task LogQsoAsync_AlwaysPersists_RegardlessOfGridTrackerOrQrzOutcome()
    {
        var repository = new FakeLogbookRepository();
        var gridTracker = new FakeGridTrackerStreamer { ReturnValue = false };
        var qrz = new FakeQrzLogbookUploader { ReturnValue = new QrzUploadResult(false, null, "rejected") };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzUploadSettings.SectionKey,
                new QrzUploadSettings { Enabled = true, ApiKey = "key" },
                QrzUploadSettingsJsonContext.Default.QrzUploadSettings),
        };
        var service = CreateService(repository, gridTracker, qrz, settingsStore);

        var result = await service.LogQsoAsync(SampleRecord());

        Assert.Single(repository.Records);
        Assert.False(result.GridTrackerSent);
        Assert.False(result.QrzUploaded);
        Assert.Equal("rejected", result.QrzError);
    }

    [Fact]
    public async Task LogQsoAsync_RepositoryThrows_PropagatesAndNeverCallsGridTrackerOrQrz()
    {
        var repository = new FakeLogbookRepository { ThrowOnAdd = new InvalidOperationException("disk full") };
        var gridTracker = new FakeGridTrackerStreamer();
        var qrz = new FakeQrzLogbookUploader();
        var service = CreateService(repository, gridTracker, qrz);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LogQsoAsync(SampleRecord()));

        Assert.Equal(0, gridTracker.CallCount);
        Assert.Equal(0, qrz.CallCount);
    }

    [Fact]
    public async Task LogQsoAsync_GridTrackerStreamerIsAlwaysCalled_GatingIsInternalToTheStreamer()
    {
        // LogbookSessionService never reads GridTrackerStreamingSettings itself -- the real
        // GridTrackerStreamer gates on its own Enabled setting internally and just returns false
        // when disabled, so the session service always calls it unconditionally.
        var gridTracker = new FakeGridTrackerStreamer { ReturnValue = false };
        var service = CreateService(gridTrackerStreamer: gridTracker);

        await service.LogQsoAsync(SampleRecord());

        Assert.Equal(1, gridTracker.CallCount);
    }

    [Fact]
    public async Task LogQsoAsync_QrzDisabled_NeverCallsTheUploader()
    {
        var qrz = new FakeQrzLogbookUploader();
        var service = CreateService(qrzUploader: qrz); // default FakeSettingsStore has no QrzUpload section

        var result = await service.LogQsoAsync(SampleRecord());

        Assert.Equal(0, qrz.CallCount);
        Assert.False(result.QrzUploaded);
    }

    [Fact]
    public async Task LogQsoAsync_QrzEnabledButNoApiKey_NeverCallsTheUploader()
    {
        var qrz = new FakeQrzLogbookUploader();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzUploadSettings.SectionKey,
                new QrzUploadSettings { Enabled = true, ApiKey = null },
                QrzUploadSettingsJsonContext.Default.QrzUploadSettings),
        };
        var service = CreateService(qrzUploader: qrz, settingsStore: settingsStore);

        await service.LogQsoAsync(SampleRecord());

        Assert.Equal(0, qrz.CallCount);
    }

    [Fact]
    public async Task LogQsoAsync_QrzEnabledWithApiKey_CallsTheUploaderWithTheConfiguredKey()
    {
        var qrz = new FakeQrzLogbookUploader { ReturnValue = new QrzUploadResult(true, "42", null) };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzUploadSettings.SectionKey,
                new QrzUploadSettings { Enabled = true, ApiKey = "my-key" },
                QrzUploadSettingsJsonContext.Default.QrzUploadSettings),
        };
        var service = CreateService(qrzUploader: qrz, settingsStore: settingsStore);

        var result = await service.LogQsoAsync(SampleRecord());

        Assert.Equal(1, qrz.CallCount);
        Assert.Equal("my-key", qrz.LastApiKey);
        Assert.True(result.QrzUploaded);
    }

    [Fact]
    public async Task LogQsoAsync_BuildsAdifTextWithStationCallsignFromOperatorSettings()
    {
        var gridTracker = new FakeGridTrackerStreamer();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                OperatorSettings.SectionKey,
                new OperatorSettings { Callsign = "w1aw" },
                OperatorSettingsJsonContext.Default.OperatorSettings),
        };
        var service = CreateService(gridTrackerStreamer: gridTracker, settingsStore: settingsStore);

        await service.LogQsoAsync(SampleRecord());

        Assert.Contains("<STATION_CALLSIGN:4>W1AW", gridTracker.LastAdifText);
        Assert.Contains("<CALL:6>N0CALL", gridTracker.LastAdifText);
        Assert.Contains("<EOR>", gridTracker.LastAdifText);
    }

    [Fact]
    public async Task SearchAsync_DelegatesToTheRepository()
    {
        var repository = new FakeLogbookRepository();
        await repository.AddAsync(SampleRecord("1"));
        var service = CreateService(repository);

        var results = await service.SearchAsync(new LogbookQuery());

        Assert.Single(results);
    }

    [Fact]
    public async Task UpdateQsoAsync_DelegatesToTheRepository()
    {
        var repository = new FakeLogbookRepository();
        await repository.AddAsync(SampleRecord("1"));
        var service = CreateService(repository);
        var edited = SampleRecord("1") with { Notes = "edited" };

        await service.UpdateQsoAsync(edited);

        Assert.Single(repository.Records);
        Assert.Equal("edited", repository.Records[0].Notes);
    }

    [Fact]
    public async Task UpdateQsoAsync_NeverPushesToGridTrackerOrQrz()
    {
        var repository = new FakeLogbookRepository();
        await repository.AddAsync(SampleRecord("1"));
        var gridTracker = new FakeGridTrackerStreamer();
        var qrz = new FakeQrzLogbookUploader();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzUploadSettings.SectionKey,
                new QrzUploadSettings { Enabled = true, ApiKey = "my-key" },
                QrzUploadSettingsJsonContext.Default.QrzUploadSettings),
        };
        var service = CreateService(repository, gridTracker, qrz, settingsStore);

        await service.UpdateQsoAsync(SampleRecord("1") with { Notes = "edited" });

        Assert.Equal(0, gridTracker.CallCount);
        Assert.Equal(0, qrz.CallCount);
    }

    [Fact]
    public async Task ExportAdifFileAsync_WritesEveryMatchingRecordToARealFile()
    {
        var repository = new FakeLogbookRepository();
        await repository.AddAsync(SampleRecord("1"));
        await repository.AddAsync(SampleRecord("2"));
        var service = CreateService(repository);
        var path = Path.Combine(Path.GetTempPath(), $"scanline-studio-export-test-{Guid.NewGuid()}.adi");

        try
        {
            await service.ExportAdifFileAsync(path, new LogbookQuery());
            var content = await File.ReadAllTextAsync(path);

            Assert.Contains("<EOH>", content);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(content, "<EOR>").Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportAdifFileAsync_IncludesStationCallsignFromOperatorSettings()
    {
        var repository = new FakeLogbookRepository();
        await repository.AddAsync(SampleRecord("1"));
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                OperatorSettings.SectionKey,
                new OperatorSettings { Callsign = "w1aw" },
                OperatorSettingsJsonContext.Default.OperatorSettings),
        };
        var service = CreateService(repository, settingsStore: settingsStore);
        var path = Path.Combine(Path.GetTempPath(), $"scanline-studio-export-test-{Guid.NewGuid()}.adi");

        try
        {
            await service.ExportAdifFileAsync(path, new LogbookQuery());
            var content = await File.ReadAllTextAsync(path);

            Assert.Contains("<STATION_CALLSIGN:4>W1AW", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAdifFileAsync_ParsesAndPersistsEveryRecordFromARealFile()
    {
        var repository = new FakeLogbookRepository();
        var service = CreateService(repository);
        var path = Path.Combine(Path.GetTempPath(), $"scanline-studio-import-test-{Guid.NewGuid()}.adi");
        await File.WriteAllTextAsync(path, "<EOH>" +
            "<CALL:4>W1AW<QSO_DATE:8>20260101<TIME_ON:4>1200<EOR>" +
            "<CALL:6>DL2QSK<QSO_DATE:8>20260102<TIME_ON:4>1300<EOR>");

        try
        {
            var imported = await service.ImportAdifFileAsync(path);

            Assert.Equal(2, imported.Count);
            Assert.Equal(2, repository.Records.Count);
            Assert.Contains(repository.Records, r => r.Callsign == "W1AW");
            Assert.Contains(repository.Records, r => r.Callsign == "DL2QSK");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
