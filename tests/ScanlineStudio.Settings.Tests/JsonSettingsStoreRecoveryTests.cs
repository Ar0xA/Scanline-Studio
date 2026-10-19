using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Settings.Tests;

/// <summary>A damaged or unreadable settings.json must never be silently replaced by defaults, and every
/// save keeps the file it replaces as settings.json.bak.</summary>
public sealed partial class JsonSettingsStoreTests
{
    private const string GoodJson = """{"SchemaVersion":1,"Sections":{"Sample":{"Name":"kept","Value":7}}}""";

    private string BackupFilePath => _settingsFilePath + ".bak";

    private static string[] CorruptCopies(string path) =>
        Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*");

    private JsonSettingsStore CreateStore() => new(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);

    private static SampleSection? Sample(AppSettings settings) =>
        settings.GetSection(SampleSection.SectionKey, SampleSectionJsonContext.Default.SampleSection);

    private static AppSettings WithSample(AppSettings settings, string name) =>
        settings.WithSection(SampleSection.SectionKey, new SampleSection(name, 1), SampleSectionJsonContext.Default.SampleSection);

    /// <summary>chmod has no effect for root, so permission tests would pass vacuously there.</summary>
    private static bool CanReadDespiteNoPermissions(string path)
    {
        try
        {
            using var _ = File.OpenRead(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static TheoryData<byte[]> CorruptContents => new()
    {
        "{not json"u8.ToArray(),
        Array.Empty<byte>(),
        new byte[64],
        "null"u8.ToArray(),
        """{"Sections":null}"""u8.ToArray(),
    };

    [Theory]
    [MemberData(nameof(CorruptContents))]
    public async Task Load_CorruptFile_NoBackup_MovesItAsideAndResetsToDefaults(byte[] content)
    {
        await File.WriteAllBytesAsync(_settingsFilePath, content);
        using var store = CreateStore();

        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.Sections);
        Assert.False(File.Exists(_settingsFilePath));
        Assert.Equal(content, await File.ReadAllBytesAsync(Assert.Single(CorruptCopies(_settingsFilePath))));
        Assert.Equal(SettingsRecoveryKind.ResetToDefaults, store.TakeNotice()?.Kind);

        await store.UpdateAsync(s => WithSample(s, "fresh"));
        Assert.Equal("fresh", Sample(await CreateStore().LoadAsync())?.Name);
        Assert.Single(CorruptCopies(_settingsFilePath));
    }

    [Theory]
    [MemberData(nameof(CorruptContents))]
    public async Task Load_CorruptFile_GoodBackup_RestoresTheBackupOnDisk(byte[] content)
    {
        await File.WriteAllBytesAsync(_settingsFilePath, content);
        await File.WriteAllTextAsync(BackupFilePath, GoodJson);
        using var store = CreateStore();

        var loaded = await store.LoadAsync();

        Assert.Equal("kept", Sample(loaded)?.Name);
        Assert.Equal(GoodJson, await File.ReadAllTextAsync(_settingsFilePath));
        Assert.Equal(GoodJson, await File.ReadAllTextAsync(BackupFilePath));
        Assert.Equal(content, await File.ReadAllBytesAsync(Assert.Single(CorruptCopies(_settingsFilePath))));
        Assert.Equal(SettingsRecoveryKind.Restored, store.TakeNotice()?.Kind);
        Assert.Null(store.TakeNotice());

        // A second store, as the next startup would: the restore was persisted, not served from memory.
        using var next = CreateStore();
        Assert.Equal("kept", Sample(await next.LoadAsync())?.Name);
        Assert.Null(next.TakeNotice());
    }

    [Fact]
    public async Task Load_CorruptFileAndCorruptBackup_MovesBothAsideAndResetsToDefaults()
    {
        await File.WriteAllTextAsync(_settingsFilePath, "{bad");
        await File.WriteAllTextAsync(BackupFilePath, "null");
        using var store = CreateStore();

        var loaded = await store.LoadAsync();

        Assert.Empty(loaded.Sections);
        Assert.Single(CorruptCopies(_settingsFilePath));
        Assert.Single(CorruptCopies(BackupFilePath));
        Assert.False(File.Exists(BackupFilePath));
        Assert.Equal(SettingsRecoveryKind.ResetToDefaults, store.TakeNotice()?.Kind);
    }

    [Fact]
    public async Task Load_CorruptTwice_KeepsBothCopies()
    {
        using var store = CreateStore();
        await File.WriteAllTextAsync(_settingsFilePath, "{first");
        await store.LoadAsync();
        await File.WriteAllTextAsync(_settingsFilePath, "{second");
        await store.LoadAsync();

        var contents = CorruptCopies(_settingsFilePath).Select(File.ReadAllText).Order().ToArray();
        Assert.Equal(["{first", "{second"], contents);
    }

    [Fact]
    public async Task Load_MissingFile_GoodBackup_RestoresTheBackup()
    {
        await File.WriteAllTextAsync(BackupFilePath, GoodJson);
        using var store = CreateStore();

        Assert.Equal("kept", Sample(await store.LoadAsync())?.Name);
        Assert.Equal(GoodJson, await File.ReadAllTextAsync(_settingsFilePath));
        Assert.Equal(SettingsRecoveryKind.Restored, store.TakeNotice()?.Kind);
    }

    [Fact]
    public async Task Load_MissingFile_CorruptBackup_MovesItAsideAndResetsToDefaults()
    {
        await File.WriteAllTextAsync(BackupFilePath, "{bad");
        using var store = CreateStore();

        Assert.Empty((await store.LoadAsync()).Sections);
        Assert.False(File.Exists(BackupFilePath));
        Assert.Single(CorruptCopies(BackupFilePath));
        Assert.Equal(SettingsRecoveryKind.ResetToDefaults, store.TakeNotice()?.Kind);
    }

    [Fact]
    public async Task Load_NoFiles_IsAPlainFreshInstall()
    {
        using var store = CreateStore();

        Assert.Empty((await store.LoadAsync()).Sections);
        Assert.Null(store.TakeNotice());
        await store.UpdateAsync(s => WithSample(s, "first"));
        Assert.False(File.Exists(BackupFilePath));
    }

    [Fact]
    public async Task Save_KeepsTheReplacedFileAsBackup()
    {
        using var store = CreateStore();
        await store.UpdateAsync(s => WithSample(s, "one"));
        var firstBytes = await File.ReadAllBytesAsync(_settingsFilePath);

        await store.UpdateAsync(s => WithSample(s, "two"));

        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(BackupFilePath));
        Assert.Equal("two", Sample(await store.LoadAsync())?.Name);
        Assert.False(File.Exists(BackupFilePath + ".tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(BackupFilePath));
        }
    }

    [Fact]
    public async Task Save_BackupCannotBeWritten_SaveStillSucceeds()
    {
        using var store = CreateStore();
        await store.UpdateAsync(s => WithSample(s, "one"));
        Directory.CreateDirectory(BackupFilePath); // a directory where the backup file should go

        await store.UpdateAsync(s => WithSample(s, "two"));

        Assert.Equal("two", Sample(await CreateStore().LoadAsync())?.Name);
    }

    [SkipOnWindowsFact]
    public async Task UnreadableFile_RefusesChanges_UntilReadableAgain()
    {
        // For CA1416, which does not understand [SkipOnWindowsFact].
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await File.WriteAllTextAsync(_settingsFilePath, GoodJson);
        File.SetUnixFileMode(_settingsFilePath, UnixFileMode.None);
        if (CanReadDespiteNoPermissions(_settingsFilePath))
        {
            return;
        }

        using var store = CreateStore();
        try
        {
            Assert.Empty((await store.LoadAsync()).Sections);
            Assert.Equal(SettingsRecoveryKind.Unavailable, store.TakeNotice()?.Kind);

            // A no-op mutate (read-and-maybe-write callers) still works.
            var unchanged = await store.UpdateAsync(s => s);
            Assert.Empty(unchanged.Sections);

            await Assert.ThrowsAsync<SettingsUnavailableException>(() => store.UpdateAsync(s => WithSample(s, "lost")));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => store.SaveAsync(new AppSettings()));
        }
        finally
        {
            File.SetUnixFileMode(_settingsFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(GoodJson, await File.ReadAllTextAsync(_settingsFilePath));
        Assert.Equal("kept", Sample(await store.LoadAsync())?.Name);
        await store.UpdateAsync(s => WithSample(s, "now"));
        Assert.Equal("now", Sample(await store.LoadAsync())?.Name);
    }

    [SkipOnWindowsFact]
    public async Task UnreadableFile_EachLoadCountsAsAFallbackServe_ReadableLoadsDoNot()
    {
        // For CA1416, which does not understand [SkipOnWindowsFact].
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await File.WriteAllTextAsync(_settingsFilePath, GoodJson);
        using var store = CreateStore();
        await store.LoadAsync();
        Assert.Equal(0, store.FallbackServeCount);

        File.SetUnixFileMode(_settingsFilePath, UnixFileMode.None);
        if (CanReadDespiteNoPermissions(_settingsFilePath))
        {
            return;
        }

        try
        {
            await store.LoadAsync();
            await store.LoadAsync();
            Assert.Equal(2, store.FallbackServeCount);
        }
        finally
        {
            File.SetUnixFileMode(_settingsFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await store.LoadAsync();
        Assert.Equal(2, store.FallbackServeCount);
    }

    [SkipOnWindowsFact]
    public async Task CorruptFile_UnreadableBackup_RefusesChanges_BackupSurvivesTwoSaves()
    {
        // For CA1416, which does not understand [SkipOnWindowsFact].
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await File.WriteAllTextAsync(_settingsFilePath, "{bad");
        await File.WriteAllTextAsync(BackupFilePath, GoodJson);
        File.SetUnixFileMode(BackupFilePath, UnixFileMode.None);
        if (CanReadDespiteNoPermissions(BackupFilePath))
        {
            return;
        }

        using var store = CreateStore();
        try
        {
            await store.LoadAsync();
            Assert.Equal(SettingsRecoveryKind.Unavailable, store.TakeNotice()?.Kind);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => store.UpdateAsync(s => WithSample(s, "a")));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => store.UpdateAsync(s => WithSample(s, "b")));
        }
        finally
        {
            File.SetUnixFileMode(BackupFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal(GoodJson, await File.ReadAllTextAsync(BackupFilePath));
        // Readable again: the next load restores it.
        Assert.Equal("kept", Sample(await store.LoadAsync())?.Name);
    }

    [SkipOnWindowsFact]
    public async Task CorruptFile_CannotBeMovedAside_ServesBackupReadOnly()
    {
        // For CA1416, which does not understand [SkipOnWindowsFact].
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await File.WriteAllTextAsync(_settingsFilePath, "{bad");
        await File.WriteAllTextAsync(BackupFilePath, GoodJson);
        var directory = Path.GetDirectoryName(_settingsFilePath)!;
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var store = CreateStore();
        try
        {
            if (CanCreateFileIn(directory))
            {
                return;
            }

            Assert.Equal("kept", Sample(await store.LoadAsync())?.Name);
            Assert.Equal(SettingsRecoveryKind.Unavailable, store.TakeNotice()?.Kind);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => store.UpdateAsync(s => WithSample(s, "x")));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal("{bad", await File.ReadAllTextAsync(_settingsFilePath));
        Assert.Empty(CorruptCopies(_settingsFilePath));
    }

    private static bool CanCreateFileIn(string directory)
    {
        var probe = Path.Combine(directory, "probe");
        try
        {
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task CorruptFile_RestoreCannotBeStaged_ServesBackupReadOnly_BackupIntact()
    {
        await File.WriteAllTextAsync(_settingsFilePath, "{bad");
        await File.WriteAllTextAsync(BackupFilePath, GoodJson);
        Directory.CreateDirectory(_settingsFilePath + ".restore.tmp"); // blocks the staged copy
        using var store = CreateStore();

        Assert.Equal("kept", Sample(await store.LoadAsync())?.Name);
        Assert.Equal(SettingsRecoveryKind.Unavailable, store.TakeNotice()?.Kind);
        await Assert.ThrowsAsync<SettingsUnavailableException>(() => store.UpdateAsync(s => WithSample(s, "x")));
        Assert.Equal(GoodJson, await File.ReadAllTextAsync(BackupFilePath));
    }

    [Fact]
    public async Task Relocate_MovesTheBackupToo_EvenWithoutASettingsFile()
    {
        await File.WriteAllTextAsync(BackupFilePath, GoodJson);
        var newDirectory = Path.Combine(Path.GetDirectoryName(_settingsFilePath)!, "moved");
        Directory.CreateDirectory(newDirectory);
        await File.WriteAllTextAsync(Path.Combine(newDirectory, "settings.json.bak"), "stale");
        using var store = CreateStore();

        var (moved, _) = await store.RelocateAsync(newDirectory);

        Assert.True(moved);
        Assert.False(File.Exists(BackupFilePath));
        Assert.Equal(GoodJson, await File.ReadAllTextAsync(Path.Combine(newDirectory, "settings.json.bak")));
    }

    [Fact]
    public async Task Relocate_MovesSettingsAndBackup()
    {
        using var store = CreateStore();
        await store.UpdateAsync(s => WithSample(s, "one"));
        await store.UpdateAsync(s => WithSample(s, "two"));
        var newDirectory = Path.Combine(Path.GetDirectoryName(_settingsFilePath)!, "moved");

        var (moved, _) = await store.RelocateAsync(newDirectory);

        Assert.True(moved);
        Assert.True(File.Exists(Path.Combine(newDirectory, "settings.json")));
        Assert.True(File.Exists(Path.Combine(newDirectory, "settings.json.bak")));
        Assert.False(File.Exists(BackupFilePath));
    }
}
