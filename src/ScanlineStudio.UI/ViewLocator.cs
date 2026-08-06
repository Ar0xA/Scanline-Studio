using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public partial class ViewLocator : IDataTemplate
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

        // Declared via bare XAML (`<local:ViewLocator />` in App.axaml), not DI-constructed --
        // resolved lazily from App.Services here, same pattern MainWindow.axaml.cs already uses for
        // the same class of problem. A view failing to resolve used to render as literal fallback
        // text with zero trace anywhere in the log.
        if (App.Services?.GetService<ILogger<ViewLocator>>() is { } logger)
        {
            Log.ViewNotFound(logger, name);
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "View not found for type: {TypeName}")]
        public static partial void ViewNotFound(ILogger logger, string typeName);
    }
}
