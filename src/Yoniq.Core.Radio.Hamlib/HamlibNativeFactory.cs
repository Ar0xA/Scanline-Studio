namespace Yoniq.Core.Radio.Hamlib;

/// <summary>Real <see cref="IHamlibNativeFactory"/> -- constructs the actual <see cref="HamlibNative"/>.</summary>
internal sealed class HamlibNativeFactory : IHamlibNativeFactory
{
    public IHamlibNative Create(INativeLibraryLoader loader, nint handle) => new HamlibNative(loader, handle);
}
