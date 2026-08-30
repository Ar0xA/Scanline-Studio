using System.Reflection;
using System.Runtime.InteropServices;
using ScanlineStudio.Core.Radio.OmniRig;

namespace ScanlineStudio.Core.Radio.Tests;

/// <summary>Pins <see cref="OmniRigComClient"/>'s CLSID/IID/<c>[DispId]</c> literals against the
/// legacy source they were transcribed from -- <c>yoniq-old/YONIQ-main/OmniRig_TLB.cpp</c> (GUIDs)
/// and <c>OmniRig_TLB.h</c> (DISPIDs). Pure attribute-metadata reflection, no COM activation
/// involved -- runs on any OS, including this Linux dev/CI machine, unlike the real client itself.
///
/// This is the only thing in the suite that can catch a marshaling regression here:
/// <see cref="FakeOmniRigComClient"/> can't, by construction -- it never touches these attributes at
/// all. Every literal below was independently re-transcribed from the legacy header/source (not
/// copy-pasted from <see cref="OmniRigComClient"/> itself), so a bug present in both places would
/// still be caught.</summary>
public sealed class OmniRigComClientAttributeTests
{
    // Interface members are implicitly public within the interface itself, even though the
    // interfaces here are declared `private` as nested types -- BindingFlags.NonPublic alone (which
    // would be needed to find the *type*, if we were looking it up by name rather than already
    // holding it) does not match a member reflection lookup on the type once we have it; Public is
    // required for the member query itself.
    private const BindingFlags InterfaceMember = BindingFlags.Public | BindingFlags.Instance;

    private static readonly Type OmniRigXInterface =
        typeof(OmniRigComClient).GetNestedType("IOmniRigXComInterface", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("IOmniRigXComInterface nested type not found -- rename?");

    private static readonly Type RigXInterface =
        typeof(OmniRigComClient).GetNestedType("IRigXComInterface", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("IRigXComInterface nested type not found -- rename?");

    // OmniRig_TLB.cpp:46 -- CLSID_OmniRigX.
    [Fact]
    public void ClsidOmniRigX_MatchesLegacyTlbCpp()
    {
        var field = typeof(OmniRigComClient).GetField("ClsidOmniRigX", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ClsidOmniRigX field not found -- rename?");

        Assert.Equal("0839E8C6-ED30-4950-8087-966F970F0CAE", (string)field.GetRawConstantValue()!,
            ignoreCase: true);
    }

    // OmniRig_TLB.cpp:44 -- IID_IOmniRigX.
    [Fact]
    public void IOmniRigXComInterface_GuidAttribute_MatchesLegacyTlbCpp()
    {
        var guid = OmniRigXInterface.GetCustomAttribute<GuidAttribute>()
            ?? throw new InvalidOperationException("GuidAttribute missing on IOmniRigXComInterface.");

        Assert.Equal("501A2858-3331-467A-837A-989FDEDACC7D", guid.Value, ignoreCase: true);
    }

    [Fact]
    public void IOmniRigXComInterface_IsDispatchInterface()
    {
        var interfaceType = OmniRigXInterface.GetCustomAttribute<InterfaceTypeAttribute>()
            ?? throw new InvalidOperationException("InterfaceTypeAttribute missing on IOmniRigXComInterface.");

        Assert.Equal(ComInterfaceType.InterfaceIsIDispatch, interfaceType.Value);
    }

    // OmniRig_TLB.h:1135 -- IOmniRigX.Rig1, DISPID 3.
    [Fact]
    public void IOmniRigXComInterface_Rig1_HasDispId3()
    {
        var property = OmniRigXInterface.GetProperty("Rig1", InterfaceMember)
            ?? throw new InvalidOperationException("Rig1 property not found -- rename?");

        AssertDispId(property, 3);
    }

    // OmniRig_TLB.cpp:47 -- IID_IRigX.
    [Fact]
    public void IRigXComInterface_GuidAttribute_MatchesLegacyTlbCpp()
    {
        var guid = RigXInterface.GetCustomAttribute<GuidAttribute>()
            ?? throw new InvalidOperationException("GuidAttribute missing on IRigXComInterface.");

        Assert.Equal("D30A7E51-5862-45B7-BFFA-6415917DA0CF", guid.Value, ignoreCase: true);
    }

    [Fact]
    public void IRigXComInterface_IsDispatchInterface()
    {
        var interfaceType = RigXInterface.GetCustomAttribute<InterfaceTypeAttribute>()
            ?? throw new InvalidOperationException("InterfaceTypeAttribute missing on IRigXComInterface.");

        Assert.Equal(ComInterfaceType.InterfaceIsIDispatch, interfaceType.Value);
    }

    // OmniRig_TLB.h:1670 -- IRigX.ReadableParams, DISPID 2.
    [Fact]
    public void IRigXComInterface_ReadableParams_HasDispId2() =>
        AssertDispId(GetRigXProperty("ReadableParams"), 2);

    // OmniRig_TLB.h:1686 -- IRigX.WriteableParams, DISPID 3.
    [Fact]
    public void IRigXComInterface_WriteableParams_HasDispId3() =>
        AssertDispId(GetRigXProperty("WriteableParams"), 3);

    // OmniRig_TLB.h -- IRigX.Status, DISPID 6.
    [Fact]
    public void IRigXComInterface_Status_HasDispId6() =>
        AssertDispId(GetRigXProperty("Status"), 6);

    // OmniRig_TLB.h -- IRigX.StatusStr, DISPID 7.
    [Fact]
    public void IRigXComInterface_StatusStr_HasDispId7() =>
        AssertDispId(GetRigXProperty("StatusStr"), 7);

    // OmniRig_TLB.h:1770-1772 -- IRigX.Freq, DISPID 8, VT_I4 (32-bit) on the wire.
    [Fact]
    public void IRigXComInterface_Freq_HasDispId8() =>
        AssertDispId(GetRigXProperty("Freq"), 8);

    [Fact]
    public void IRigXComInterface_Freq_IsInt32NotInt64()
    {
        // The doc comment on the real property is explicit that this must never widen to `long` --
        // pin the CLR property type itself, not just the DISPID, so a well-meaning "future-proofing"
        // widen would fail here instead of silently reinterpreting every wire value.
        Assert.Equal(typeof(int), GetRigXProperty("Freq").PropertyType);
    }

    // OmniRig_TLB.h -- IRigX.Tx (PTT), DISPID 17.
    [Fact]
    public void IRigXComInterface_Tx_HasDispId17() =>
        AssertDispId(GetRigXProperty("Tx"), 17);

    // OmniRig_TLB.h -- IRigX.Mode, DISPID 18. A distinct property from Tx, never conflated with it.
    [Fact]
    public void IRigXComInterface_Mode_HasDispId18() =>
        AssertDispId(GetRigXProperty("Mode"), 18);

    private static PropertyInfo GetRigXProperty(string name) =>
        RigXInterface.GetProperty(name, InterfaceMember)
        ?? throw new InvalidOperationException($"{name} property not found on IRigXComInterface -- rename?");

    private static void AssertDispId(MemberInfo member, int expectedDispId)
    {
        var dispId = member.GetCustomAttribute<DispIdAttribute>()
            ?? throw new InvalidOperationException($"DispIdAttribute missing on {member.Name}.");

        Assert.Equal(expectedDispId, dispId.Value);
    }
}
