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
        FakeAdifUdpStreamer? adifUdpStreamer = null,
        FakeQrzLogbookUploader? qrzUploader = null,
        FakeQrzCallsignLookup? qrzLookup = null,
        FakeSettingsStore? settingsStore = null)
    {
        return new LogbookSessionService(
            repository ?? new FakeLogbookRepository(),
            new AdifExporter(),
            new AdifImporter(),
            adifUdpStreamer ?? new FakeAdifUdpStreamer(),
            qrzUploader ?? new FakeQrzLogbookUploader(),
            qrzLookup ?? new FakeQrzCallsignLookup(),
            settingsStore ?? new FakeSettingsStore(),
            NullLogger<LogbookSessionService>.Instance);
    }

    [Fact]
    public async Task LogQsoAsync_AlwaysPersists_RegardlessOfAdifUdpOrQrzOutcome()
    {
        var repository = new FakeLogbookRepository();
        var adifUdp = new FakeAdifUdpStreamer { ResultToReturn = new AdifUdpSendResult(0, 1) };
        var qrz = new FakeQrzLogbookUploader { ReturnValue = new QrzUploadResult(false, null, "rejected") };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzUploadSettings.SectionKey,
                new QrzUploadSettings { Enabled = true, ApiKey = "key" },
                QrzUploadSettingsJsonContext.Default.QrzUploadSettings),
        };
        var service = CreateService(repository, adifUdp, qrz, settingsStore: settingsStore);

        var result = await service.LogQsoAsync(SampleRecord());

        Assert.Single(repository.Records);
        Assert.Equal(0, result.AdifUdpSentCount);
        Assert.Equal(1, result.AdifUdpEnabledCount);
        Assert.False(result.QrzUploaded);
        Assert.Equal("rejected", result.QrzError);
    }

    [Fact]
    public async Task LogQsoAsync_RepositoryThrows_PropagatesAndNeverCallsAdifUdpOrQrz()
    {
        var repository = new FakeLogbookRepository { ThrowOnAdd = new InvalidOperationException("disk full") };
        var adifUdp = new FakeAdifUdpStreamer();
        var qrz = new FakeQrzLogbookUploader();
        var service = CreateService(repository, adifUdp, qrz);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LogQsoAsync(SampleRecord()));

        Assert.Equal(0, adifUdp.CallCount);
        Assert.Equal(0, qrz.CallCount);
    }

    [Fact]
    public async Task LogQsoAsync_PostPersistStepThrows_StillReturnsThePersistedRecord_DoesNotThrow()
    {
        // Closes a real bug flagged by Tier A Batch 10 chunk 10c (docs/functional-audit-playbook.md):
        // this method's own doc comment claims everything after persistence is "best-effort and must
        // never ... block on it" -- but a throw from the ADIF-UDP/settings/export span used to
        // propagate OUT of LogQsoAsync AFTER the record was already committed, and both real UI
        // callers treat a thrown exception as "logging failed," re-enabling their own retry
        // affordance -- a genuine duplicate QSO on retry, since the first attempt's persistence
        // already succeeded. The QSO must still come back as successfully logged.
        var repository = new FakeLogbookRepository();
        var adifUdp = new FakeAdifUdpStreamer { ExceptionToThrow = new IOException("network unreachable") };
        var service = CreateService(repository, adifUdp);

        var result = await service.LogQsoAsync(SampleRecord());

        Assert.Single(repository.Records);
        Assert.Equal(repository.Records[0].Id, result.Record.Id);
        Assert.Equal(0, result.AdifUdpSentCount);
        Assert.False(result.QrzUploaded);
    }

    [Fact]
    public async Task LogQsoAsync_AdifUdpStreamerIsAlwaysCalled_GatingIsInternalToTheStreamer()
    {
        // LogbookSessionService never reads AdifUdpStreamingSettings itself -- the real
        // AdifUdpStreamer gates per-destination on its own settings section internally and just
        // returns a 0-sent result when nothing is enabled, so the session service always calls it
        // unconditionally.
        var adifUdp = new FakeAdifUdpStreamer { ResultToReturn = new AdifUdpSendResult(0, 0) };
        var service = CreateService(adifUdpStreamer: adifUdp);

        await service.LogQsoAsync(SampleRecord());

        Assert.Equal(1, adifUdp.CallCount);
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
        var adifUdp = new FakeAdifUdpStreamer();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                OperatorSettings.SectionKey,
                new OperatorSettings { Callsign = "w1aw" },
                OperatorSettingsJsonContext.Default.OperatorSettings),
        };
        var service = CreateService(adifUdpStreamer: adifUdp, settingsStore: settingsStore);

        await service.LogQsoAsync(SampleRecord());

        Assert.Contains("<STATION_CALLSIGN:4>W1AW", adifUdp.LastAdifText);
        Assert.Contains("<CALL:6>N0CALL", adifUdp.LastAdifText);
        Assert.Contains("<EOR>", adifUdp.LastAdifText);
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
    public async Task UpdateQsoAsync_NeverPushesToAdifUdpOrQrz()
    {
        var repository = new FakeLogbookRepository();
        await repository.AddAsync(SampleRecord("1"));
        var adifUdp = new FakeAdifUdpStreamer();
        var qrz = new FakeQrzLogbookUploader();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzUploadSettings.SectionKey,
                new QrzUploadSettings { Enabled = true, ApiKey = "my-key" },
                QrzUploadSettingsJsonContext.Default.QrzUploadSettings),
        };
        var service = CreateService(repository, adifUdp, qrz, settingsStore: settingsStore);

        await service.UpdateQsoAsync(SampleRecord("1") with { Notes = "edited" });

        Assert.Equal(0, adifUdp.CallCount);
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

    [Fact]
    public async Task LookupCallsignAsync_NotConfigured_ReturnsFailure_NeverCallsTheLookupClient()
    {
        var qrzLookup = new FakeQrzCallsignLookup();
        var service = CreateService(qrzLookup: qrzLookup); // default FakeSettingsStore has no QrzLookup section

        var result = await service.LookupCallsignAsync("W1AW");

        Assert.False(result.Success);
        Assert.Equal("QRZ lookup is not configured in Options.", result.ErrorReason);
        Assert.Equal(0, qrzLookup.LookupCallCount);
    }

    [Fact]
    public async Task LookupCallsignAsync_EnabledButNoUsername_ReturnsFailure_NeverCallsTheLookupClient()
    {
        var qrzLookup = new FakeQrzCallsignLookup();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzLookupSettings.SectionKey,
                new QrzLookupSettings { Enabled = true, Username = null, Password = "pass" },
                QrzLookupSettingsJsonContext.Default.QrzLookupSettings),
        };
        var service = CreateService(qrzLookup: qrzLookup, settingsStore: settingsStore);

        var result = await service.LookupCallsignAsync("W1AW");

        Assert.False(result.Success);
        Assert.Equal(0, qrzLookup.LookupCallCount);
    }

    [Fact]
    public async Task LookupCallsignAsync_EnabledWithCredentials_DelegatesToTheLookupClient()
    {
        var qrzLookup = new FakeQrzCallsignLookup
        {
            LookupReturnValue = new QrzCallsignLookupResult(true, "Hiram Maxim", "Newington (United States)", "FN31pr", null),
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzLookupSettings.SectionKey,
                new QrzLookupSettings { Enabled = true, Username = "user", Password = "pass" },
                QrzLookupSettingsJsonContext.Default.QrzLookupSettings),
        };
        var service = CreateService(qrzLookup: qrzLookup, settingsStore: settingsStore);

        var result = await service.LookupCallsignAsync("W1AW");

        Assert.True(result.Success);
        Assert.Equal("Hiram Maxim", result.Name);
        Assert.Equal(1, qrzLookup.LookupCallCount);
        Assert.Equal("W1AW", qrzLookup.LastCallsign);
        Assert.Equal("user", qrzLookup.LastUsername);
        Assert.Equal("pass", qrzLookup.LastPassword);
    }

    [Fact]
    public async Task LookupCallsignAsync_SettingsLoadThrows_ReturnsFailureInsteadOfThrowing()
    {
        // Tier B audit finding: this method's own interface doc comment claims the same "never
        // throws, always returns a result" contract as LogQsoAsync, but the settings read used to run
        // unguarded -- a hand-edited settings.json (malformed QrzLookup section, or an unreadable
        // file) would throw straight out of this method instead of returning a result.
        var qrzLookup = new FakeQrzCallsignLookup();
        var settingsStore = new FakeSettingsStore { LoadAsyncException = new UnauthorizedAccessException("access denied") };
        var service = CreateService(qrzLookup: qrzLookup, settingsStore: settingsStore);

        var result = await service.LookupCallsignAsync("W1AW");

        Assert.False(result.Success);
        Assert.Equal("access denied", result.ErrorReason);
        Assert.Equal(0, qrzLookup.LookupCallCount);
    }

    [Fact]
    public async Task IsQrzLookupConfiguredAsync_NotConfigured_ReturnsFalse()
    {
        var service = CreateService(); // default FakeSettingsStore has no QrzLookup section

        Assert.False(await service.IsQrzLookupConfiguredAsync());
    }

    [Fact]
    public async Task IsQrzLookupConfiguredAsync_EnabledButNoUsername_ReturnsFalse()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzLookupSettings.SectionKey,
                new QrzLookupSettings { Enabled = true, Username = null, Password = "pass" },
                QrzLookupSettingsJsonContext.Default.QrzLookupSettings),
        };
        var service = CreateService(settingsStore: settingsStore);

        Assert.False(await service.IsQrzLookupConfiguredAsync());
    }

    [Fact]
    public async Task IsQrzLookupConfiguredAsync_EnabledWithCredentials_ReturnsTrue()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzLookupSettings.SectionKey,
                new QrzLookupSettings { Enabled = true, Username = "user", Password = "pass" },
                QrzLookupSettingsJsonContext.Default.QrzLookupSettings),
        };
        var service = CreateService(settingsStore: settingsStore);

        Assert.True(await service.IsQrzLookupConfiguredAsync());
    }

    [Fact]
    public async Task IsQrzLookupConfiguredAsync_SavedButDisabled_ReturnsFalse()
    {
        // Same reasoning as LookupCallsignAsync_EnabledButNoUsername_... above -- credentials
        // being saved doesn't mean the user actually turned lookup on.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                QrzLookupSettings.SectionKey,
                new QrzLookupSettings { Enabled = false, Username = "user", Password = "pass" },
                QrzLookupSettingsJsonContext.Default.QrzLookupSettings),
        };
        var service = CreateService(settingsStore: settingsStore);

        Assert.False(await service.IsQrzLookupConfiguredAsync());
    }

    [Fact]
    public async Task IsQrzLookupConfiguredAsync_SettingsLoadThrows_ReturnsFalseInsteadOfThrowing()
    {
        // Same "never throws" contract as LookupCallsignAsync_SettingsLoadThrows_... above -- a
        // settings-read failure must resolve to "not configured", not propagate to a UI gate.
        var settingsStore = new FakeSettingsStore { LoadAsyncException = new UnauthorizedAccessException("access denied") };
        var service = CreateService(settingsStore: settingsStore);

        Assert.False(await service.IsQrzLookupConfiguredAsync());
    }

    [Fact]
    public async Task TestQrzLookupCredentialsAsync_UngatedByEnabledSetting_AlwaysDelegatesToTheLookupClient()
    {
        // Deliberately ungated -- this IS the settings-configuration flow itself, testing values
        // the user hasn't saved (or enabled) yet.
        var qrzLookup = new FakeQrzCallsignLookup { TestReturnValue = new QrzLoginResult(true, null) };
        var service = CreateService(qrzLookup: qrzLookup); // default FakeSettingsStore: QrzLookup not even present

        var result = await service.TestQrzLookupCredentialsAsync("user", "pass");

        Assert.True(result.Success);
        Assert.Equal(1, qrzLookup.TestCallCount);
        Assert.Equal("user", qrzLookup.LastUsername);
        Assert.Equal("pass", qrzLookup.LastPassword);
    }
}
