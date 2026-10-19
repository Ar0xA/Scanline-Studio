using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScanlineStudio.Settings.Tests;

public sealed partial class AppSettingsSectionExtensionsTests
{
    public sealed record Sample(int Port);

    [JsonSerializable(typeof(Sample))]
    private sealed partial class SampleJsonContext : JsonSerializerContext;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static AppSettings With(string key, string json) => new() { Sections = new() { [key] = Json(json) } };

    [Fact]
    public void GetSection_MalformedSection_StillThrows()
    {
        Assert.ThrowsAny<JsonException>(() => With("S", "{\"Port\":\"abc\"}").GetSection("S", SampleJsonContext.Default.Sample));
    }

    [Fact]
    public void TryReadSection_Malformed_ReturnsFalseWithTheError()
    {
        Assert.False(With("S", "{\"Port\":\"abc\"}").TryReadSection("S", SampleJsonContext.Default.Sample, out var value, out var error));
        Assert.Null(value);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryReadSection_MissingOrNullOrValid_ReturnsTrue()
    {
        Assert.True(new AppSettings().TryReadSection("S", SampleJsonContext.Default.Sample, out var missing, out _));
        Assert.Null(missing);
        Assert.True(With("S", "null").TryReadSection("S", SampleJsonContext.Default.Sample, out var jsonNull, out _));
        Assert.Null(jsonNull);
        Assert.True(With("S", "{\"Port\":7}").TryReadSection("S", SampleJsonContext.Default.Sample, out var valid, out var error));
        Assert.Equal(7, valid!.Port);
        Assert.Null(error);
    }

    [Fact]
    public void SetSectionAside_MovesTheRawSection_ReplacesAnEarlierCopy_AndNeverMutatesTheInput()
    {
        var input = With("S", "{\"Port\":\"abc\"}");
        input.Sections["S.unreadable"] = Json("{\"old\":1}");

        var result = input.SetSectionAside("S");

        Assert.False(result.Sections.ContainsKey("S"));
        Assert.Equal("{\"Port\":\"abc\"}", result.Sections["S.unreadable"].GetRawText());
        Assert.True(input.Sections.ContainsKey("S"));
        Assert.Equal("{\"old\":1}", input.Sections["S.unreadable"].GetRawText());
    }

    [Fact]
    public void SetSectionAside_AbsentKey_ReturnsTheInputUnchanged()
    {
        var input = new AppSettings();
        Assert.Same(input, input.SetSectionAside("S"));
    }
}
