using System.IO;
using PulseWorkshop.App.Mvvm;

namespace PulseWorkshop.App.ViewModels;

/// <summary>One row in an "Open recent" list: a remembered file (an advanced project or an Unpack
/// archive), shown by its file name with its folder as a muted second line, with a command that
/// reopens it. The owning view model supplies the reopen callback.</summary>
public sealed class RecentItemViewModel
{
    public RecentItemViewModel(string path, Action<string> open)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        OpenCommand = new RelayCommand(() => open(path));
    }

    /// <summary>The full path reopened when the row is clicked (also the row tooltip).</summary>
    public string Path { get; }

    /// <summary>The file name shown on the row.</summary>
    public string Name { get; }

    /// <summary>The containing folder, shown muted under the name.</summary>
    public string Directory { get; }

    public RelayCommand OpenCommand { get; }
}
