using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio.Flrig;

namespace ScanlineStudio.Core.Radio.Tests;

public sealed class FlrigModeTokensTests
{
    [Fact]
    public void Candidates_NoTokenAppearsInMoreThanOneRadioModesList()
    {
        // Closes the round-2 auditor finding: a mechanical inversion of the write-direction candidate
        // table has no stated tie-break if a literal token could plausibly appear in more than one
        // RadioMode's list. This test is what makes the separate, hand-authored TokenToMode dictionary
        // trustworthy -- if it ever drifts, this fails loudly instead of silently misclassifying a read.
        var seenIn = new Dictionary<string, RadioMode>(StringComparer.Ordinal);

        foreach (var (mode, tokens) in FlrigModeTokens.Candidates)
        {
            foreach (var token in tokens)
            {
                Assert.False(
                    seenIn.TryGetValue(token, out var owner),
                    $"Token '{token}' appears in both {owner}'s and {mode}'s candidate lists.");
                seenIn[token] = mode;
            }
        }
    }

    [Fact]
    public void TokenToMode_IsConsistentWithCandidates_EveryTokenMapsBackToItsOwningMode()
    {
        foreach (var (mode, tokens) in FlrigModeTokens.Candidates)
        {
            foreach (var token in tokens)
            {
                Assert.True(FlrigModeTokens.TokenToMode.TryGetValue(token, out var mapped), $"Token '{token}' has no TokenToMode entry.");
                Assert.Equal(mode, mapped);
            }
        }
    }

    [Theory]
    [InlineData(RadioMode.Usb)]
    [InlineData(RadioMode.Lsb)]
    [InlineData(RadioMode.Am)]
    [InlineData(RadioMode.Fm)]
    [InlineData(RadioMode.Cw)]
    [InlineData(RadioMode.CwR)]
    [InlineData(RadioMode.Rtty)]
    [InlineData(RadioMode.RttyR)]
    [InlineData(RadioMode.Data)]
    [InlineData(RadioMode.DataR)]
    [InlineData(RadioMode.Pkt)]
    public void Candidates_EveryRadioModeExceptUnknownHasAtLeastOneToken(RadioMode mode)
    {
        Assert.True(FlrigModeTokens.Candidates.TryGetValue(mode, out var tokens));
        Assert.NotEmpty(tokens);
    }
}
