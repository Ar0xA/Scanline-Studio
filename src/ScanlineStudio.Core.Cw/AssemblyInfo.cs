using System.Runtime.CompilerServices;

// Same reasoning as ScanlineStudio.Core.Sstv's own AssemblyInfo.cs: lets the test project write
// focused unit tests directly against internal DSP building blocks (MorseAlphabet, the classical
// decoder's own timing steps) rather than only through the public ICwIdDecoder.DecodeAsync round-trip
// -- a round-trip test alone (encode via CwMorseGenerator, decode via ClassicalCwDecoder) can pass
// while both halves are wrong the same way, the exact failure class CLAUDE.md §4 exists to prevent.
[assembly: InternalsVisibleTo("ScanlineStudio.Core.Cw.Tests")]
