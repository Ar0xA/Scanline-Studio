using System.Runtime.CompilerServices;

// Tests need direct access to RigParamXMapper and the IOmniRigComClient seam to exercise them in
// isolation without a real COM install -- same InternalsVisibleTo pattern
// ScanlineStudio.Core.Radio.Hamlib already uses for IHamlibNative.
[assembly: InternalsVisibleTo("ScanlineStudio.Core.Radio.Tests")]
