# CAT Layer (per-rig protocol implementations)

## Related

[[02-radio-layer]] (implements `IRadioProtocol`) · sibling of → [[04-rigctld]] · replaces the `Freq*` methods and command tables in `cradio.cpp`, `ExtCmd.cpp`

## Purpose

Implement `IRadioProtocol` for each concrete CAT dialect. Each protocol is a small, independently testable class that turns wire bytes into `RadioState` and turns commands into wire bytes — nothing else. This is the direct replacement for `CCradio::FreqYaesuHF`, `FreqYaesuVU`, `FreqICOM`, `FreqKenwood`, `FreqJST245`, `FreqYaesu9K2K` and the `CmdInit`/`CmdRx`/`CmdTx` string templates in `RadioSet`.

## Protocol families to port

| Family | Legacy source | Notes |
|---|---|---|
| Yaesu HF (FT-1000D/920/1000MP) | `FreqYaesuHF` | **Not BCD** (corrected after review — see below): a fixed 5-byte frame whose bytes 1–4 are a big-endian 32-bit binary value, divided by a **per-model floating-point divisor** (`25600000.0` for FT1000D, `1000000.0` for FT920, `1600000.0` for FT1000MP) to yield MHz (`UpdateFreq` formats the result `%.3f`). The divisor table is data, not a formula — port it as a lookup, not an inferred pattern. |
| Yaesu newer (FT-950/2000/9000/450) | `FreqYaesu9K2K` | Extended binary command set, per-radio quirks table — verify framing per model against source before assuming it matches `FreqYaesuHF`'s shape |
| Yaesu VHF/UHF | `FreqYaesuVU` | Fixed 3-byte frame, genuinely BCD (nibble-pairs, big-endian byte order), scaled `/1000.0` for MHz — this one *is* the BCD case; don't conflate it with Yaesu HF above |
| Icom CI-V | `FreqICOM` | Addressed bus protocol (multiple rigs can share one CI-V bus). Frame is **variable-length**: BCD digits read from byte offset 8 down to offset 4, but byte 8 only contributes a 10th digit when it isn't the `0xfd` frame terminator (`cradio.cpp:982`) — i.e. the digit count depends on frame content, not a fixed schema. "Negative" variant flips S-meter/PTT polarity. |
| Kenwood (TS-x) | `FreqKenwood` | ASCII command protocol (`FA`, `FB`, `MD`, ...) — closest to what rigctld/Hamlib itself speaks natively; frames are terminated by `;` and gated on a leading `IF` response of at least 13 bytes (`cradio.cpp:854-867`) |
| Ten-Tec Omni VI | (via generic poll table) | ASCII, Kenwood-adjacent |
| JRC JST-245 | `FreqJST245` | ASCII, distinct framing, frame-started on leading `I` byte |

Each gets its own class in `Yoniq.Core.Radio.Cat`, e.g. `IcomCivProtocol : IRadioProtocol`, `YaesuHfBinaryProtocol : IRadioProtocol` (per-model divisor table, *not* BCD), `YaesuVuBcdProtocol : IRadioProtocol` (genuinely BCD), `KenwoodAsciiProtocol : IRadioProtocol`. Radios sharing a wire format but differing only in address/quirks (e.g. all Icom rigs) are configured via a `RigQuirks` data object passed to a shared protocol class rather than one class per model — avoids the legacy pattern of near-duplicated `Freq*` methods per rig variant. Do not assume any two `Freq*` methods share a framing scheme without checking `cradio.cpp` directly — Yaesu HF and Yaesu VU look similar at a glance (both "Yaesu", both fixed-size frames) but use different encodings entirely, which is exactly the kind of mistake this section exists to head off.

## Framing helpers

Binary and ASCII framing/parsing helpers (BCD encode/decode, checksum, delimiter scanning) live in a shared `Yoniq.Core.Radio.Cat.Framing` internal namespace so protocol classes stay declarative:

```csharp
internal static class Bcd
{
    // For genuinely BCD frames (Yaesu VU, Icom CI-V). NOT used for Yaesu HF — see table above.
    // digitOrder lets callers express both fixed-length (VU: most-significant byte first)
    // and variable-length, terminator-gated (CI-V: least-significant byte first, digit count
    // determined by a frame terminator byte, not a fixed schema) layouts explicitly.
    public static long Decode(ReadOnlySpan<byte> bcd, BcdDigitOrder digitOrder);
    public static void Encode(long value, Span<byte> destination, BcdDigitOrder digitOrder);
}

internal static class BinaryFrequency
{
    // For Yaesu HF-style frames: big-endian 32-bit binary, scaled by a per-model divisor.
    public static double DecodeMhz(ReadOnlySpan<byte> frame, double modelDivisor);
}
```

**Precision note**: the legacy pipeline is `double`-precision end-to-end and formats the final frequency as text (`%.3f` MHz) before ever comparing values (`UpdateFreq`, `cradio.cpp:918-928`) — it never compares raw binary frequency values directly. [[02-radio-layer]]'s `RadioState.FrequencyHz` is an integer `long`. Going from a `double` MHz intermediate to integer Hz requires a documented rounding rule (round-half-to-even at the Hz boundary is the recommended default); pick one, write it down here, and cover it with a golden-vector test (see [[13-testing]]) rather than leaving the rounding behavior implicit in whatever `(long)(mhz * 1_000_000)` happens to produce for a given rig's divisor.

## Generic command-template fallback

Legacy `RadioSet` allowed users to define a rig via free-text `CmdInit`/`CmdRx`/`CmdTx`/`cmdGNR` templates with placeholder substitution — an escape hatch for unsupported/未知 rigs. The rewrite keeps this as `TemplateCatProtocol : IRadioProtocol`, driven by a user-editable JSON template (see [[12-settings]]) instead of raw C-string format specifiers, so users are not blocked waiting on a first-party protocol implementation.

**Binary-is-bytes rule applies here directly**: legacy `CmdInit`/`CmdRx`/`CmdTx`/`cmdGNR` are declared `AnsiString` (`cradio.h:48-52`) but carry raw CAT command bytes, not text — values ≥0x80 and embedded nulls are legitimate frame content, not encoding artifacts. The JSON template format must represent these as an explicit byte sequence (e.g. a hex string field, `"initHex": "FE FE 94 E0 03 FD"`) with placeholder substitution operating on the byte sequence, never as a JSON string literal holding the raw characters — a naive UTF-8 JSON string would corrupt any byte ≥0x80 and truncate at an embedded null. See CLAUDE.md's binary-is-bytes rule.

## Custom/user-defined commands

`ExtCmd.cpp` allowed arbitrary extra CAT command strings bound to UI buttons (band-specific macros, etc.). This becomes `IRadioController.SendRawAsync(byte[] frame)` — an escape hatch exposed to the plugin layer ([[11-plugin-system]]) and macro/button UI ([[09-ui]]), not part of the core `IRadioProtocol` contract, so raw command support never has to be special-cased into every protocol implementation.

## Transport binding

CAT protocols in this layer only ever talk to `IRadioTransport` (defined in [[02-radio-layer]]); the concrete transport is a serial port (`SerialTransport : IRadioTransport`, wrapping `System.IO.Ports.SerialPort`) for all rigs in this document. TCP-native rig control (e.g. rigs with a built-in Ethernet CAT server) reuses the same `IRadioProtocol` contract over a `TcpTransport : IRadioTransport` — no protocol code changes needed to move a rig from serial to network, which is the entire point of separating transport from protocol.

## Testing

Every protocol class is tested purely against byte fixtures — no serial port, no timer:

```csharp
[Fact]
public async Task IcomCiv_Poll_ParsesFrequencyAndMode()
{
    var transport = new FakeRadioTransport(replayBytes: IcomFixtures.FreqResponse_14205300_USB);
    var sut = new IcomCivProtocol(address: 0x94);

    var state = await sut.PollAsync(transport, CancellationToken.None);

    state.FrequencyHz.Should().Be(14_205_300);
    state.Mode.Should().Be(RadioMode.Usb);
}
```

Fixture byte sequences are captured once from real hardware (or from Hamlib's own test vectors where license-compatible) and checked into `tests/Yoniq.Core.Radio.Tests/Fixtures/`. Per CLAUDE.md's behavioral-parity rule, every fixture used for a `Freq*`-equivalent parity check should also record the legacy code's expected string output (e.g. `"14.070"` from `UpdateFreq`'s `%.3f` formatting) alongside the raw bytes, so the assertion is against a real captured legacy value, not a value re-derived by re-reading the C++ a second time — the whole point of a golden vector is to not trust either implementation's arithmetic in isolation.

## Definition of done

- [ ] `TemplateCatProtocol` implemented and covers at least the legacy default rig set via migrated templates.
- [ ] Icom CI-V and Kenwood ASCII protocols implemented and fixture-tested (chosen first: CI-V exercises addressed-bus framing, Kenwood ASCII is the closest analog to rigctld's own wire format, easing later cross-checking against [[04-rigctld]]).
- [ ] Remaining Yaesu variants and JST-245 implemented and fixture-tested.
- [ ] Every protocol class has ≥1 fixture-based round-trip test (poll + at least one `Set*Async`).
