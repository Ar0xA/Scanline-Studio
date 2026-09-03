using System.Runtime.CompilerServices;

// Lets the test project write focused unit tests directly against internal DSP building blocks
// (VisHeader, YCbCr) rather than only through the public encode/decode round-trip -- important for
// this codebase specifically, since CLAUDE.md's behavioral-parity rule calls for reference-value
// checks with a stated tolerance, not just self-consistency, and several real bugs here (the
// YCbCr +128 offset, RM12's forced VIS parity bit) were only findable that way: a round-trip test
// alone can pass while both the encoder and decoder are wrong in the same direction.
[assembly: InternalsVisibleTo("ScanlineStudio.Core.Sstv.Tests")]

// Round-9 D2-audit finding: ScanlineStudio.Host.Tests' SstvCompositionRootTests needs the
// RestartableSstvDecoder.Inner*ForTests accessors to prove the actual DI composition root
// (ScanlineStudio.Host.Program.CreateSstvDecoder) forwards every persisted SstvDecoderSettings
// field into the resolved decoder -- without this, a dropped constructor argument at the
// composition root (not just inside CreateInner's periodic-restart rebuild) would leave that
// suite green while a user's Options > Decode setting silently never took effect at all.
[assembly: InternalsVisibleTo("ScanlineStudio.Host.Tests")]

// fsk_cwid.md B-P1: ScanlineStudio.Core.Cw.Tests needs CwMorseGenerator.Generate (internal, already
// a verified port of legacy WriteCWID) to synthesize realistic CW test audio for ClassicalCwDecoder
// -- re-implementing tone-segment generation independently in the test project would risk a SECOND,
// unverified encoder that could silently drift from the real one, undermining the exact
// "round-trip alone can pass while both halves are wrong" risk CLAUDE.md §4 exists to guard against.
[assembly: InternalsVisibleTo("ScanlineStudio.Core.Cw.Tests")]
