using System.IO;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// One node in a read-only on-disk folder tree (the Package - Simple tab's content preview).
/// Unlike the Unpack tab's tree this shows files and folders in the same tree and can't export,
/// rename or delete anything - it only previews what a pack would include.
///
/// Children are loaded on first access, which for a TreeView means "when the parent is expanded"
/// (WPF only realizes an item's container - and so its ItemsSource binding - once its parent
/// expands). Deep trees therefore cost one directory listing per opened folder, not a full walk.
/// </summary>
public sealed class FileTreeNodeViewModel : ObservableObject
{
    private readonly long _size;
    private IReadOnlyList<FileTreeNodeViewModel>? _children;
    private bool _isExpanded;

    private FileTreeNodeViewModel(FileSystemInfo info, bool isDirectory)
    {
        FullPath = info.FullName;
        Name = info.Name;
        IsDirectory = isDirectory;
        _size = info is FileInfo f ? f.Length : 0;
        RevealCommand = new RelayCommand(() => ShellOpen.Reveal(FullPath));
    }

    /// <summary>The root node for <paramref name="folder"/>, expanded, or null when it doesn't exist.</summary>
    public static FileTreeNodeViewModel? ForFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        var dir = new DirectoryInfo(folder.TrimEnd('\\', '/'));
        return dir.Exists ? new FileTreeNodeViewModel(dir, isDirectory: true) { IsExpanded = true } : null;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }

    public RelayCommand RevealCommand { get; }

    public IReadOnlyList<FileTreeNodeViewModel> Children => _children ??= Load();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    /// <summary>Extension chip ("VMT", "MDL", ...); blank for folders and extension-less files.</summary>
    public string TypeDisplay => IsDirectory ? string.Empty
        : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();

    /// <summary>File size; blank for folders (a rolled-up size would mean walking the whole tree).</summary>
    public string SizeDisplay => IsDirectory ? string.Empty : UnpackViewModel.FormatSize(_size);

    public string ToolTipText => FullPath;

    private IReadOnlyList<FileTreeNodeViewModel> Load()
    {
        if (!IsDirectory)
            return Array.Empty<FileTreeNodeViewModel>();
        try
        {
            var dir = new DirectoryInfo(FullPath);
            return dir.EnumerateDirectories()
                      .Select(d => new FileTreeNodeViewModel(d, isDirectory: true))
                      .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                      .Concat(dir.EnumerateFiles()
                                 .Select(f => new FileTreeNodeViewModel(f, isDirectory: false))
                                 .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
                      .ToList();
        }
        catch (Exception)
        {
            // Unreadable folder (permissions, vanished mid-browse): show it as empty.
            return Array.Empty<FileTreeNodeViewModel>();
        }
    }
}
