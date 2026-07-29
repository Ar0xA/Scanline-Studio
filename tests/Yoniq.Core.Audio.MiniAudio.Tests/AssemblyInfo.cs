using Xunit;

// This assembly's tests manipulate real, shared system audio state (pactl virtual sinks/modules,
// the process-wide native miniaudio context) -- xunit's default parallelization runs different
// test classes concurrently in the same process, which caused a real, observed flake: two test
// classes each creating/destroying their own uniquely-named virtual sink still interfered with
// each other's device enumeration, since both see the SAME system-wide device list. Disabled
// rather than working around it test-by-test, since more hardware-touching tests are coming in
// later pieces (Audio 5-8) that would hit the same interference.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
