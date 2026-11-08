namespace EZTool.Core;

/// <summary>ViewModels, die beim Öffnen ihrer Seite reagieren sollen (z. B. erster Scan).</summary>
public interface IActivatable
{
    void OnActivated();
}
