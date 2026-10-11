using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed partial class TxEditorElementRoundTripTests
{
    private static readonly string[] InheritedPerspectiveProperties =
    [
        "Corner0X", "Corner0Y", "Corner1X", "Corner1Y", "Corner2X", "Corner2Y", "Corner3X", "Corner3Y",
        "PerspectiveEnabled", "Locked", "RotationDegrees",
    ];

    [Theory]
    [InlineData(typeof(BoxElementViewModel))]
    [InlineData(typeof(ImageElementViewModel))]
    public void Inventory_StillListsThePerspectivePropertiesInheritedFromTheBase(Type type)
    {
        var inventoried = InventoriedProperties(type).ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var name in InheritedPerspectiveProperties)
        {
            Assert.True(inventoried.TryGetValue(name, out var property), $"{type.Name}.{name} is not inventoried");
            Assert.Equal(typeof(PerspectiveElementViewModelBase), property!.DeclaringType);
            Assert.True(ValueTable[type].ContainsKey(name), $"{type.Name}.{name} has no value-table entry");
        }
    }
}
