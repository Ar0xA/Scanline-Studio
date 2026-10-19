using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Drm;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>One unreadable settings section that only tunes receive must not stop receive or file decode;
/// an unreadable Audio section still does (it picks the device and TX level).</summary>
public sealed class SstvSessionServiceUnreadableSectionTests
{
    private const int Rate = 8000;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static JsonElement Broken() => JsonDocument.Parse("\"broken\"").RootElement.Clone();

    private static void Corrupt(SstvSessionServiceReceiveDrmTests.Harness h, string key, JsonElement raw) =>
        h.Settings.Settings.Sections[key] = raw;

    private static void AssertWarnedAbout(SstvSessionServiceReceiveDrmTests.Harness h, string key) =>
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains($"Settings section {key} is unreadable", StringComparison.Ordinal));

    private static void AssertUntouched(SstvSessionServiceReceiveDrmTests.Harness h, string key, JsonElement raw)
    {
        Assert.Equal(raw.GetRawText(), h.Settings.Settings.Sections[key].GetRawText());
        Assert.False(h.Settings.Settings.Sections.ContainsKey(key + ".unreadable"));
    }

    [Fact]
    public async Task UnreadableDrmRx_AfterTurningDrmOff_ReceiveStarts_AndDrmStaysOff()
    {
        var h = SstvSessionServiceReceiveDrmTests.Create();
        await h.Service.SetDrmReceiveEnabledAsync(false);
        var raw = Broken();
        Corrupt(h, DrmRxSettings.SectionKey, raw);

        await h.Service.StartReceivingAsync();

        Assert.False(h.Service.IsDrmReceiveActive);
        Assert.Empty(h.Factory.Created);
        AssertWarnedAbout(h, DrmRxSettings.SectionKey);
        AssertUntouched(h, DrmRxSettings.SectionKey, raw);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task UnreadableDrmRx_AfterTurningDrmOff_FileDecodeRuns_WithoutADrmPass()
    {
        var h = SstvSessionServiceReceiveDrmTests.Create();
        await h.Service.SetDrmReceiveEnabledAsync(false);
        var raw = Broken();
        Corrupt(h, DrmRxSettings.SectionKey, raw);
        string path = Path.Combine(Path.GetTempPath(), $"drm-unreadable-{Guid.NewGuid():N}.wav");
        WavFile.Write(path, new float[8000], Rate);
        try
        {
            await h.Service.DecodeFromFileAsync(path).WaitAsync(Wait);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(h.Factory.Created);
        AssertWarnedAbout(h, DrmRxSettings.SectionKey);
        AssertUntouched(h, DrmRxSettings.SectionKey, raw);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task UnreadableDrmRx_FreshSession_ReceiveStartsWithDrmAtItsDefault()
    {
        var h = SstvSessionServiceReceiveDrmTests.Create();
        Corrupt(h, DrmRxSettings.SectionKey, Broken());

        await h.Service.StartReceivingAsync();

        Assert.Equal(DrmRxSettings.DefaultRxEnabled, h.Service.IsDrmReceiveActive);
        AssertWarnedAbout(h, DrmRxSettings.SectionKey);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task UnreadableStationId_ReceiveStarts_WithTheDefaultStationIdDecodeToggles()
    {
        var h = SstvSessionServiceReceiveDrmTests.Create();
        var raw = Broken();
        Corrupt(h, StationIdSettings.SectionKey, raw);

        await h.Service.StartReceivingAsync();

        Assert.Equal(StationIdSettings.DefaultFskIdRxEnabled, h.Decoder.StationIdDecodeEnabled);
        var cwIdRxEnabled = (bool)typeof(SstvSessionService).GetField("_cwIdRxEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(h.Service)!;
        Assert.Equal(StationIdSettings.DefaultCwIdRxEnabled, cwIdRxEnabled);
        AssertWarnedAbout(h, StationIdSettings.SectionKey);
        AssertUntouched(h, StationIdSettings.SectionKey, raw);
        await h.Service.DisposeAsync();
    }

    [Fact]
    public async Task UnreadableAudio_StillStopsReceive()
    {
        var h = SstvSessionServiceReceiveDrmTests.Create();
        Corrupt(h, AudioDeviceSettings.SectionKey, Broken());

        await Assert.ThrowsAsync<JsonException>(() => h.Service.StartReceivingAsync());
        await h.Service.DisposeAsync();
    }
}
