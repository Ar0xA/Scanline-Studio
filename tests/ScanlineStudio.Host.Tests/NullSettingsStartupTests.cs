using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Host.Tests;

public sealed class NullSettingsStartupTests
{
    [Fact]
    public async Task StartupDecoderResolution_NullSectionsOnDisk_UsesDefaults()
    {
        var directory = Directory.CreateTempSubdirectory("scanline-null-settings-").FullName;
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, """{"Sections":null}""");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, path));
            // Use the same registration and decoder factory as real startup, with the real
            // source-generated settings-file reader. No stand-in settings DTO or store.
            Program.RegisterSstvServices(services);
            using var provider = services.BuildServiceProvider();

            var decoder = Assert.IsType<RestartableSstvDecoder>(provider.GetRequiredService<ISstvDecoder>());

            Assert.Equal(SstvSampleRate.Default, decoder.SampleRate);
            Assert.True(decoder.AutoSlantEnabled);
            Assert.Same(decoder, provider.GetRequiredService<ISstvDecoder>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
