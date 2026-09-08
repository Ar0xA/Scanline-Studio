using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

public sealed class LogbookSessionServiceTests
{
    private static QsoRecord SampleRecord(string id = "1") =>
        new(id, "N0CALL", DateTimeOffset.UtcNow, null, null, null, "martin1", null, null, null, null, null, null, null, null, false, false);

    private static LogbookSessionService CreateService(
        FakeLogbookRepository? repository = null,
        FakeAdifUdpStreamer? adifUdpStreamer = null,
        FakeQrzLogbookUploader? qrzUploader = null,
        FakeQrzCallsignLookup? qrzLookup = null,
        FakeSettingsStore? settingsStore = null,
        FakeReceiveHistoryStoreForLogbook? receiveHistoryStore = null,
        IAdifExporter? adifExporter = null)
    {
        return new LogbookSessionService(
            repository ?? new FakeLogbookRepository(),
            adifExporter ?? new AdifExporter(),
            new AdifImporter(),
            adifUdpStreamer ?? new FakeAdifUdpStreamer(),
            qrzUploader ?? new FakeQrzLogbookUploader(),
            qrzLookup ?? new FakeQrzCallsignLookup(),
            settingsStore ?? new FakeSettingsStore(),
            receiveHistoryStore ?? new FakeReceiveHistoryStoreForLogbook(),
            NullLogger<LogbookSessionService>.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportAdifFileAsync_FailureAfterWritingPreservesPreviousExport(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scanline-adif-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "previous.adi");
            await File.WriteAllTextAsync(path, "Previous good export");
            using var cancellation = new CancellationTokenSource();
            var service = CreateService(adifExporter: new CallbackAdifExporter(writer =>
            {
                writer.Write("Partial replacement export");
                writer.Flush();
                if (cancel)
                {
                    cancellation.Cancel();
                }
                else
                {
                    throw new IOException("Injected failure after flushing partial export");
                }
            }));

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAdifFileAsync(path, new LogbookQuery(), cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => service.ExportAdifFileAsync(path, new LogbookQuery()));
            }

            Assert.Equal("Previous good export", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CallbackAdifExporter(Action<TextWriter> write) : IAdifExporter
    {
        public void Export(IEnumerable<QsoRecord> records, TextWriter writer, string? stationCallsign = null) => write(writer);
    }

    [Fact]
    public async Task ExportAdifFileAsync_WriterDisposalFailurePreservesPreviousExport()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scanline-adif-disposal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "previous.adi");
            await File.WriteAllTextAsync(path, "Previous good export");
            var service = CreateService(adifExporter: new CallbackAdifExporter(writer =>
            {
                writer.Write("Buffered export awaiting final flush");
                // Disposal must flush this buffered text into a stream that now rejects writes.
                Assert.IsType<StreamWriter>(writer).BaseStream.Dispose();
            }));

            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ExportAdifFileAsync(path, new LogbookQuery()));

            Assert.Equal("Previous good export", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
        // T1-7 (production_audit.md): this used to be hardcoded null -- the real failure reason
        // (already logged server-side) never reached the caller, only that SOMETHING failed.
        //
        // Auditor code-review finding (2026-08-31): the reason belongs in PostPersistError, not
        // QrzError -- this throw is from ADIF-UDP, not QRZ, and QrzError renders through a
        // QRZ-specific "QRZ: failed (...)" UI string regardless of the real cause.
        Assert.Null(result.QrzError);
        Assert.Equal("network unreachable", result.PostPersistError);
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
            await File.WriteAllTextAsync(path, "Previous export");
            await service.ExportAdifFileAsync(path, new LogbookQuery());
            var content = await File.ReadAllTextAsync(path);

            Assert.DoesNotContain("Previous export", content);
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
    public async Task ImportAdifFileAsync_Windows1252SourceFileWithNoBom_DecodesAccentedFieldCorrectly()
    {
        // T1-17 (production_audit.md): a non-UTF-8 ADIF file (e.g. Windows-1252, common from other
        // ham-logging software) with no BOM used to get silently mis-decoded as UTF-8 by
        // StreamReader's own default. Windows-1252 is a single-byte encoding, so the byte-count ADIF
        // length prefix (<NAME:4>) equals the character count here -- no multi-byte-length concern.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var repository = new FakeLogbookRepository();
        var service = CreateService(repository);
        var path = Path.Combine(Path.GetTempPath(), $"scanline-studio-import-test-{Guid.NewGuid()}.adi");
        var windows1252 = System.Text.Encoding.GetEncoding(1252);
        var adifText = "<EOH>" +
            "<CALL:4>W1AW<QSO_DATE:8>20260101<TIME_ON:4>1200<NAME:4>Jörg<EOR>";
        await File.WriteAllBytesAsync(path, windows1252.GetBytes(adifText));

        try
        {
            var imported = await service.ImportAdifFileAsync(path);

            var record = Assert.Single(imported);
            Assert.Equal("W1AW", record.Callsign);
            Assert.Equal("Jörg", record.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAdifFileAsync_Utf16SourceFileWithBom_StillParsesRecords()
    {
        // Auditor code-review finding (2026-08-31): StreamReader's own BOM detection correctly
        // decodes a UTF-16 file, but AdifImporter's byte-count length slicing is only safe for UTF-8
        // or a single-byte code page (see AdifImporter's own class doc comment) -- a multi-byte
        // encoding puts non-ASCII bytes (including embedded nulls) between the ASCII eoh/eor
        // delimiter bytes the scan looks for, silently breaking it (zero records, no exception, a
        // reported "success"). A UTF-16 ADIF file used to import fine (Import hardcoded UTF-8,
        // re-encoding the correctly-decoded text) before this regression was introduced and then
        // fixed by rejecting an unsafe caller-supplied encoding inside Import itself.
        var repository = new FakeLogbookRepository();
        var service = CreateService(repository);
        var path = Path.Combine(Path.GetTempPath(), $"scanline-studio-import-test-{Guid.NewGuid()}.adi");
        var adifText = "<EOH>" +
            "<CALL:4>W1AW<QSO_DATE:8>20260101<TIME_ON:4>1200<EOR>";
        byte[] utf16Bytes = [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes(adifText)];
        await File.WriteAllBytesAsync(path, utf16Bytes);

        try
        {
            var imported = await service.ImportAdifFileAsync(path);

            var record = Assert.Single(imported);
            Assert.Equal("W1AW", record.Callsign);
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

    // ui_transition_plan.md step 15 (QSO delete) ------------------------------------------------

    [Fact]
    public async Task DeleteQsoAsync_ClearsTheReceiveHistoryLinkBeforeDeletingTheRow()
    {
        // Round-1 plan-review blocker: clear-then-delete, not delete-then-clear -- if the process
        // dies between the two calls, the worst case must be a recoverable unlinked-but-existing
        // QSO, never a permanently dangling ReceiveHistory.LinkedQsoId.
        var repository = new FakeLogbookRepository();
        await repository.AddAsync(SampleRecord("qso-1"));
        var receiveHistoryStore = new FakeReceiveHistoryStoreForLogbook { ClearLinkedQsoIdResultToReturn = 1 };
        // Code-review finding: asserting both calls happened afterward doesn't prove ORDER (a
        // delete-then-clear implementation would pass identically) -- this hook inspects the
        // repository's state from INSIDE the clear call, before DeleteQsoAsync can have reached its
        // own repository.DeleteAsync yet.
        var repositoryDeletedIdsAtClearTime = new List<string>();
        receiveHistoryStore.OnClearLinkedQsoId = () => repositoryDeletedIdsAtClearTime.AddRange(repository.DeletedIds);
        var service = CreateService(repository, receiveHistoryStore: receiveHistoryStore);

        var deleted = await service.DeleteQsoAsync("qso-1");

        Assert.True(deleted);
        Assert.Contains("qso-1", receiveHistoryStore.ClearedQsoIds);
        Assert.Contains("qso-1", repository.DeletedIds);
        Assert.Empty(repositoryDeletedIdsAtClearTime);
    }

    [Fact]
    public async Task DeleteQsoAsync_UnknownId_ReturnsFalse_StillAttemptsTheLinkClear()
    {
        var repository = new FakeLogbookRepository();
        var receiveHistoryStore = new FakeReceiveHistoryStoreForLogbook();
        var service = CreateService(repository, receiveHistoryStore: receiveHistoryStore);

        var deleted = await service.DeleteQsoAsync("does-not-exist");

        Assert.False(deleted);
        Assert.Contains("does-not-exist", receiveHistoryStore.ClearedQsoIds);
    }

    [Fact]
    public async Task DeleteQsoAsync_ClearLinkedQsoIdThrows_LogsAndStillDeletesTheQso()
    {
        // Fail-open: a ReceiveHistory-store hiccup must never block the operator's actual request
        // (deleting the QSO).
        var repository = new FakeLogbookRepository();
        await repository.AddAsync(SampleRecord("qso-1"));
        var receiveHistoryStore = new FakeReceiveHistoryStoreForLogbook
        {
            ThrowOnClearLinkedQsoId = new InvalidOperationException("simulated DB hiccup"),
        };
        var service = CreateService(repository, receiveHistoryStore: receiveHistoryStore);

        var deleted = await service.DeleteQsoAsync("qso-1");

        Assert.True(deleted);
        Assert.Contains("qso-1", repository.DeletedIds);
    }

    [Fact]
    public async Task DeleteQsoAsync_RepositoryThrows_Propagates()
    {
        var repository = new FakeLogbookRepository { ThrowOnDelete = new InvalidOperationException("disk full") };
        await repository.AddAsync(SampleRecord("qso-1"));
        var service = CreateService(repository);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteQsoAsync("qso-1"));
    }

    // ui_transition_plan.md step 15, piece (c) (duplicate-QSO detection) ------------------------

    private static QsoRecord DuplicateCandidate(string id, string callsign, DateTimeOffset startUtc, long? frequencyHz) =>
        new(id, callsign, startUtc, null, frequencyHz, null, null, null, null, null, null, null, null, null, null, false, false);

    [Fact]
    public async Task FindLikelyDuplicateAsync_SameCallsignAndBandSameDay_ReturnsTheMatch()
    {
        // Code-review finding: DateTimeOffset.UtcNow + AddHours(-1) is flaky within an hour of UTC
        // midnight (the candidate falls into the PREVIOUS calendar day) -- fixed mid-day timestamp
        // instead, same as the day-window boundary test further below.
        var repository = new FakeLogbookRepository();
        var today = new DateTimeOffset(2026, 8, 7, 14, 0, 0, TimeSpan.Zero);
        await repository.AddAsync(DuplicateCandidate("1", "N0CALL", today.AddHours(-1), 14_230_000));
        var service = CreateService(repository);

        var duplicate = await service.FindLikelyDuplicateAsync("N0CALL", today, 14_070_000, excludeId: null);

        Assert.NotNull(duplicate);
        Assert.Equal("1", duplicate.Id);
    }

    [Fact]
    public async Task FindLikelyDuplicateAsync_SameCallsignSixMonthsAgo_DoesNotFlagAsDuplicate()
    {
        // Round-1 plan-review blocker: unbounded callsign+band matching would flag a regular sked
        // partner worked six months ago on every single new contact.
        var repository = new FakeLogbookRepository();
        var today = DateTimeOffset.UtcNow;
        await repository.AddAsync(DuplicateCandidate("1", "N0CALL", today.AddMonths(-6), 14_230_000));
        var service = CreateService(repository);

        var duplicate = await service.FindLikelyDuplicateAsync("N0CALL", today, 14_230_000, excludeId: null);

        Assert.Null(duplicate);
    }

    [Fact]
    public async Task FindLikelyDuplicateAsync_SameCallsignSameDayDifferentBand_DoesNotFlagAsDuplicate()
    {
        var repository = new FakeLogbookRepository();
        var today = DateTimeOffset.UtcNow;
        await repository.AddAsync(DuplicateCandidate("1", "N0CALL", today.AddHours(-1), 14_230_000)); // 20m
        var service = CreateService(repository);

        var duplicate = await service.FindLikelyDuplicateAsync("N0CALL", today, 7_070_000, excludeId: null); // 40m

        Assert.Null(duplicate);
    }

    [Fact]
    public async Task FindLikelyDuplicateAsync_BothFrequenciesUnknown_StillFlagsAsDuplicate_NullIsNotAWildcardMismatch()
    {
        // "No band" only matches another "no band" -- this is the one case where two null bands
        // are legitimately the SAME classification (both "unknown"), not a mismatch. Fixed mid-day
        // timestamp, not UtcNow (same flaky-near-midnight reasoning as the test above).
        var repository = new FakeLogbookRepository();
        var today = new DateTimeOffset(2026, 8, 7, 14, 0, 0, TimeSpan.Zero);
        await repository.AddAsync(DuplicateCandidate("1", "N0CALL", today.AddHours(-1), null));
        var service = CreateService(repository);

        var duplicate = await service.FindLikelyDuplicateAsync("N0CALL", today, null, excludeId: null);

        Assert.NotNull(duplicate);
    }

    [Fact]
    public async Task FindLikelyDuplicateAsync_ExcludeId_NeverFlagsItself()
    {
        var repository = new FakeLogbookRepository();
        var today = DateTimeOffset.UtcNow;
        await repository.AddAsync(DuplicateCandidate("1", "N0CALL", today, 14_230_000));
        var service = CreateService(repository);

        var duplicate = await service.FindLikelyDuplicateAsync("N0CALL", today, 14_230_000, excludeId: "1");

        Assert.Null(duplicate);
    }

    [Fact]
    public async Task FindLikelyDuplicateAsync_RepositoryThrows_FailsOpen_ReturnsNullNotException()
    {
        var repository = new FakeLogbookRepository { ThrowOnSearch = new InvalidOperationException("DB locked") };
        var service = CreateService(repository);

        var duplicate = await service.FindLikelyDuplicateAsync("N0CALL", DateTimeOffset.UtcNow, 14_230_000, excludeId: null);

        Assert.Null(duplicate);
    }

    [Fact]
    public async Task FindLikelyDuplicateAsync_QueriesOnlyTheCallsignAndSameUtcCalendarDay()
    {
        // Confirms the query is pushed down (LogbookQuery.From/To), not pulled client-side -- a
        // regression here would silently widen or narrow the window without any test noticing via
        // the match/no-match assertions above alone.
        var repository = new FakeLogbookRepository();
        var startUtc = new DateTimeOffset(2026, 8, 7, 14, 0, 0, TimeSpan.Zero);
        var service = CreateService(repository);

        await service.FindLikelyDuplicateAsync("N0CALL", startUtc, 14_230_000, excludeId: null);

        // No direct query-capture hook on FakeLogbookRepository; assert indirectly via boundary
        // records instead -- a record exactly at day-start and one exactly at day-end (23:59:59...)
        // both match, one just before day-start and one just after day-end do not.
        var dayStart = new DateTimeOffset(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);
        var dayEnd = new DateTimeOffset(2026, 8, 7, 23, 59, 59, 999, TimeSpan.Zero);
        await repository.AddAsync(DuplicateCandidate("start", "N0CALL", dayStart, 14_230_000));
        await repository.AddAsync(DuplicateCandidate("end", "N0CALL", dayEnd, 14_230_000));
        await repository.AddAsync(DuplicateCandidate("before", "N0CALL", dayStart.AddTicks(-1), 14_230_000));
        await repository.AddAsync(DuplicateCandidate("after", "N0CALL", dayEnd.AddSeconds(1), 14_230_000));

        var matchStart = await service.FindLikelyDuplicateAsync("N0CALL", startUtc, 14_230_000, excludeId: "end");
        Assert.Equal("start", matchStart?.Id);

        var matchNotBefore = await service.FindLikelyDuplicateAsync("N0CALL", startUtc, 14_230_000, excludeId: "start");
        // "end" also matches and sorts first (SearchAsync orders most-recent-first) -- "before" and
        // "after" must never be the one returned.
        Assert.NotEqual("before", matchNotBefore?.Id);
        Assert.NotEqual("after", matchNotBefore?.Id);
    }

    [Fact]
    public async Task GetWorkedBeforeAsync_NoPriorContact_ReturnsNotFound()
    {
        var service = CreateService(new FakeLogbookRepository());

        var lookup = await service.GetWorkedBeforeAsync("N0CALL");

        Assert.Equal(WorkedBeforeOutcome.NotFound, lookup.Outcome);
        Assert.Null(lookup.Info);
    }

    [Fact]
    public async Task GetWorkedBeforeAsync_OnePriorContact_ReturnsFoundWithRealBandAndCount1()
    {
        var repository = new FakeLogbookRepository();
        var startUtc = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        await repository.AddAsync(DuplicateCandidate("1", "N0CALL", startUtc, 14_230_000)); // 20m
        var service = CreateService(repository);

        var lookup = await service.GetWorkedBeforeAsync("N0CALL");

        Assert.Equal(WorkedBeforeOutcome.Found, lookup.Outcome);
        Assert.Equal(1, lookup.Info!.Count);
        Assert.Equal(startUtc, lookup.Info.LastStartUtc);
        Assert.Equal("20m", lookup.Info.LastBand);
    }

    [Fact]
    public async Task GetWorkedBeforeAsync_CaseInsensitiveCallsignMatch()
    {
        var repository = new FakeLogbookRepository();
        await repository.AddAsync(DuplicateCandidate("1", "N0CALL", DateTimeOffset.UtcNow, 14_230_000));
        var service = CreateService(repository);

        var lookup = await service.GetWorkedBeforeAsync("n0call");

        Assert.Equal(WorkedBeforeOutcome.Found, lookup.Outcome);
    }

    /// <summary>Code-review finding: an earlier version of this test used
    /// <c>FakeLogbookRepository</c>'s then-CHRONOLOGICAL ordering, which could never disagree with
    /// <c>MaxBy(r =&gt; r.StartUtc)</c> -- it stayed green even against a naive, unfixed
    /// <c>candidates[0]</c> implementation. <see cref="FakeLogbookRepository"/> now sorts on the same
    /// "O"-format STRING the real <c>SqliteLogbookRepository</c> does (see that fake's own comment),
    /// and this fixture is chosen so the two orderings genuinely disagree: lexically,
    /// <c>"...T22:00:00...+05:00"</c> sorts AHEAD of <c>"...T20:00:00...+00:00"</c> (comparing the
    /// hour digit '2' vs '0'), even though 22:00+05:00 is 17:00 UTC -- chronologically EARLIER than
    /// 20:00+00:00. A <c>candidates[0]</c> implementation would report the WRONG record (17:00 UTC,
    /// 20m) as the latest; only <c>MaxBy</c> correctly picks the real latest (20:00 UTC, 40m).</summary>
    [Fact]
    public async Task GetWorkedBeforeAsync_MultipleContacts_CountsAllAndSummarizesTheMostRecentByRealInstant()
    {
        var repository = new FakeLogbookRepository();
        var lexicallyFirstButChronologicallyEarlier = new DateTimeOffset(2026, 8, 12, 22, 0, 0, TimeSpan.FromHours(5)); // 17:00 UTC
        var reallyLatest = new DateTimeOffset(2026, 8, 12, 20, 0, 0, TimeSpan.Zero); // 20:00 UTC
        await repository.AddAsync(DuplicateCandidate("1", "N0CALL", lexicallyFirstButChronologicallyEarlier, 14_230_000)); // 20m
        await repository.AddAsync(DuplicateCandidate("2", "N0CALL", reallyLatest, 7_070_000)); // 40m
        var service = CreateService(repository);

        var lookup = await service.GetWorkedBeforeAsync("N0CALL");

        Assert.Equal(WorkedBeforeOutcome.Found, lookup.Outcome);
        Assert.Equal(2, lookup.Info!.Count);
        Assert.Equal(reallyLatest, lookup.Info.LastStartUtc);
        Assert.Equal("40m", lookup.Info.LastBand);
    }

    [Fact]
    public async Task GetWorkedBeforeAsync_UnknownFrequency_ReturnsNullBandNotAnException()
    {
        var repository = new FakeLogbookRepository();
        await repository.AddAsync(DuplicateCandidate("1", "N0CALL", DateTimeOffset.UtcNow, null));
        var service = CreateService(repository);

        var lookup = await service.GetWorkedBeforeAsync("N0CALL");

        Assert.Equal(WorkedBeforeOutcome.Found, lookup.Outcome);
        Assert.Null(lookup.Info!.LastBand);
    }

    /// <summary>Worked-before plan (2026-09-01), confirming auditor round finding: deliberately
    /// distinct from <see cref="GetWorkedBeforeAsync_NoPriorContact_ReturnsNotFound"/> -- collapsing
    /// "no prior contact" and "lookup failed" into one value (as <see cref="FindLikelyDuplicateAsync"/>'s
    /// fail-open-to-null contract does) would render a transient DB error as a false "New station"
    /// for a dupe-avoidance indicator, the harmful direction.</summary>
    [Fact]
    public async Task GetWorkedBeforeAsync_RepositoryThrows_ReturnsFailedNotNotFound()
    {
        var repository = new FakeLogbookRepository { ThrowOnSearch = new InvalidOperationException("DB locked") };
        var service = CreateService(repository);

        var lookup = await service.GetWorkedBeforeAsync("N0CALL");

        Assert.Equal(WorkedBeforeOutcome.Failed, lookup.Outcome);
        Assert.Null(lookup.Info);
    }
}
