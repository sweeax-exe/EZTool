using System.Reflection;
using System.Windows;

namespace EZTool.Core;

/// <summary>
/// Ein Modul ist eine Seite im Programm (Dashboard, Cleaner, ...).
/// Um das Tool zu erweitern: einfach eine neue Klasse anlegen, die IModule implementiert –
/// sie wird automatisch gefunden und in der Navigation angezeigt.
/// </summary>
public interface IModule
{
    /// <summary>Eindeutige ID, z. B. "dashboard".</summary>
    string Id { get; }

    /// <summary>Titel in der Navigation.</summary>
    string Title { get; }

    /// <summary>Kurzbeschreibung, die oben auf der Seite erscheint.</summary>
    string Subtitle { get; }

    /// <summary>Glyph aus "Segoe Fluent Icons", z. B. "".</summary>
    string Icon { get; }

    /// <summary>Sortierung in der Navigation (klein = oben).</summary>
    int Order { get; }

    /// <summary>Gruppe in der Navigation (Überschrift). Module mit gleicher Gruppe stehen zusammen.</summary>
    string Group => Order switch
    {
        < 100 => "Monitor",
        < 200 => "Gaming",
        < 300 => "Aufräumen",
        < 400 => "Software",
        _ => "System",
    };

    /// <summary>Erzeugt die Ansicht. Wird nur einmal aufgerufen und danach gecacht.</summary>
    FrameworkElement CreateView();
}

public static class ModuleRegistry
{
    public static IReadOnlyList<IModule> Discover()
    {
        return Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }
                        && t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (IModule)Activator.CreateInstance(t)!)
            .OrderBy(m => m.Order)
            .ToList();
    }
}
