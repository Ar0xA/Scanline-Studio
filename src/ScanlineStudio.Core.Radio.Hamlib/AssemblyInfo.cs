using System.Runtime.CompilerServices;

// IHamlibNative and its P/Invoke declarations stay internal -- HamlibRadioProtocol is the only
// intended caller. Tests need direct access to substitute FakeHamlibNative and exercise
// HamlibLibraryLocator/HamlibVersionGate/HamlibRuntime in isolation (spec/03-cat-layer.md's
// "IHamlibNative seam" section), not just through IRadioProtocol's public surface.
[assembly: InternalsVisibleTo("ScanlineStudio.Core.Radio.Tests")]
