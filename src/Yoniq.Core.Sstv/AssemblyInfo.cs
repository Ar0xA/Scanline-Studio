using System.Runtime.CompilerServices;

// Lets the test project write focused unit tests directly against internal DSP building blocks
// (VisHeader, YCbCr) rather than only through the public encode/decode round-trip -- important for
// this codebase specifically, since CLAUDE.md's behavioral-parity rule calls for reference-value
// checks with a stated tolerance, not just self-consistency, and several real bugs here (the
// YCbCr +128 offset, RM12's forced VIS parity bit) were only findable that way: a round-trip test
// alone can pass while both the encoder and decoder are wrong in the same direction.
[assembly: InternalsVisibleTo("Yoniq.Core.Sstv.Tests")]
