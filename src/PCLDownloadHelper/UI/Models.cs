using System.Windows;
using System.Windows.Controls;
using PclDownloadHelper.Core;

namespace PclDownloadHelper.UI;

/// <summary>按版本类型选择方块图标。</summary>
public sealed class VersionIconSelector : DataTemplateSelector
{
    public DataTemplate? Release { get; set; }
    public DataTemplate? Snapshot { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item is VersionItem version && version.Entry.IsRelease ? Release : Snapshot;
    }
}

/// <summary>版本列表行。</summary>
public sealed class VersionItem
{
    public VersionItem(VersionEntry entry, bool installed)
    {
        Entry = entry;
        Installed = installed;
    }

    public VersionEntry Entry { get; }
    public string Id => Entry.Id;
    public string Subtitle => $"{Entry.TypeName}  ·  {Entry.DateText}";
    public bool Installed { get; }
    public Visibility InstalledVisibility => Installed ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>加载器选项行。</summary>
public sealed class LoaderOption
{
    public LoaderKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Supported { get; set; } = true;
}
