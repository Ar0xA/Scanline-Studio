using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Core.Radio;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>ui_transition_plan.md step 6 (T2-4). Auditor code-review finding (2026-08-29): this
/// class's whole reason to exist is the <see cref="ObjectDisposedException"/> guard in
/// <see cref="RadioStateProvider.Current"/> -- a correctness claim that otherwise rested entirely
/// on <c>BehaviorSubject&lt;T&gt;.Value</c>'s documented throw behavior with zero test anchoring it,
/// on a path (<c>ReceiveHistoryRecorder.OnLineDecoded</c>) that reaches the audio drain thread
/// synchronously, outside any try/catch.</summary>
public sealed class RadioStateProviderTests
{
    [Fact]
    public void Current_BeforeAnyConnection_IsNull()
    {
        var controller = new RadioController([], NullLogger<RadioController>.Instance);
        var provider = new RadioStateProvider(controller);

        Assert.Null(provider.Current);
    }

    [Fact]
    public async Task Current_AfterControllerDisposed_ReturnsNull_DoesNotThrow()
    {
        var controller = new RadioController([], NullLogger<RadioController>.Instance);
        var provider = new RadioStateProvider(controller);

        await controller.DisposeAsync();

        Assert.Null(provider.Current);
    }
}
