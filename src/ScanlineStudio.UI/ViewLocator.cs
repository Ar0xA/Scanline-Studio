using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Model.Core;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;
        
        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }
        
        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        // IDockable (Dock's Tool/Document base) matches too: Dock's own default ToolControl/
        // DocumentControl templates render the dockable itself via ambient DataTemplate lookup,
        // not via any Context-forwarding of their own -- confirmed empirically during the Phase-3
        // pre-build spike (a dockable holding a separate plain Context object rendered its tab
        // header but a blank body) and against Dock's own DockMvvmSample, where every pane
        // view-model derives from Tool/Document directly. See the Phase-3 plan's decision #8.
        return data is ViewModelBase or IDockable;
    }
}
