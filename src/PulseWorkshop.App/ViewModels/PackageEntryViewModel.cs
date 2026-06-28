using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.Core.Models;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// One row in the Package - Advanced entries list: wraps a <see cref="PackageEntry"/> (a folder to
/// pack plus its pre-assets) and defers persistence + packaging to its owning
/// <see cref="PackageAdvancedViewModel"/>. Editing any field writes to the model and asks the parent
/// to save the shared <c>.pw_mdlproject</c>.
/// </summary>
public sealed class PackageEntryViewModel : ObservableObject
{
    private readonly PackageAdvancedViewModel _parent;
    private bool _isPackaging;
    private bool _hasError;
    private string? _lastPackagePath;

    public PackageEntryViewModel(PackageAdvancedViewModel parent, PackageEntry model)
    {
        _parent = parent;
        Model = model;

        foreach (var asset in model.Assets)
            Assets.Add(new PackageAssetViewModel(this, asset));

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        PackageThisCommand = new AsyncRelayCommand(() => _parent.PackageEntryAsync(this), () => CanPackage);
        CloneCommand = new RelayCommand(() => _parent.CloneEntry(this));
        RemoveCommand = new RelayCommand(() => _parent.RemoveEntry(this));
        GoToPackageCommand = new RelayCommand(GoToPackage, () => !string.IsNullOrEmpty(LastPackagePath));
        AddAssetCommand = new RelayCommand(AddAsset);
    }

    public PackageEntry Model { get; }

    /// <summary>The image-format and asset-kind choices, shared with each asset row via the parent.</summary>
    public IReadOnlyList<AssetKindChoice> AssetKinds => _parent.AssetKinds;
    public IReadOnlyList<ImageFormatChoice> ImageFormats => _parent.ImageFormats;

    /// <summary>Short asset count shown in the entries sidebar (e.g. "2 assets").</summary>
    public string AssetSummary => Assets.Count switch
    {
        0 => "No assets",
        1 => "1 asset",
        var n => $"{n} assets",
    };

    /// <summary>The entry's pre-assets, baked into the folder before packing.</summary>
    public ObservableCollection<PackageAssetViewModel> Assets { get; } = new();

    public RelayCommand BrowseFolderCommand { get; }
    public AsyncRelayCommand PackageThisCommand { get; }
    public RelayCommand CloneCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand GoToPackageCommand { get; }
    public RelayCommand AddAssetCommand { get; }

    /// <summary>Rebuilds the model's asset list (and each asset's regex list) from the UI collections.
    /// Called before save / package.</summary>
    public void SyncAssets() =>
        Model.Assets = Assets.Select(a => { a.SyncRegex(); return a.Model; }).ToList();

    /// <summary>Resolves an asset path against the project folder (used by asset rows).</summary>
    public string ResolveAgainst(string path) => _parent.ResolveAgainstProject(path);

    /// <summary>Stores an asset path relative to the project when possible (used by asset rows).</summary>
    public string MakeRelative(string fullPath) => _parent.MakeProjectRelative(fullPath);

    /// <summary>Persists the whole project via the parent (assets are synced first).</summary>
    public void Save()
    {
        SyncAssets();
        _parent.Save();
    }

    public string Name
    {
        get => Model.Name;
        set
        {
            if (Model.Name != (value ?? string.Empty))
            {
                Model.Name = value ?? string.Empty;
                OnPropertyChanged();
                _parent.Save();
            }
        }
    }

    public string FolderPath
    {
        get => Model.FolderPath;
        set
        {
            if (Model.FolderPath != (value ?? string.Empty))
            {
                Model.FolderPath = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResolvedFolderPath));
                _parent.Save();
                _parent.RefreshCommands();
            }
        }
    }

    public bool IncludeInAll
    {
        get => Model.IncludeInAll;
        set
        {
            if (Model.IncludeInAll != value)
            {
                Model.IncludeInAll = value;
                OnPropertyChanged();
                _parent.Save();
                _parent.RefreshCommands();
            }
        }
    }

    public string Command
    {
        get => Model.Command;
        set
        {
            if (Model.Command != (value ?? string.Empty))
            {
                Model.Command = value ?? string.Empty;
                OnPropertyChanged();
                _parent.Save();
            }
        }
    }

    /// <summary>Per-entry busy flag (drives the blue "packaging" outline).</summary>
    public bool IsPackaging
    {
        get => _isPackaging;
        set { if (SetField(ref _isPackaging, value)) PackageThisCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>True when this entry's last package failed (drives the red outline; not saved).</summary>
    public bool HasError
    {
        get => _hasError;
        set => SetField(ref _hasError, value);
    }

    /// <summary>The package produced by this entry's last successful run (drives "Go to file").</summary>
    public string? LastPackagePath
    {
        get => _lastPackagePath;
        set { if (SetField(ref _lastPackagePath, value)) GoToPackageCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>The folder resolved to an absolute path against the project folder.</summary>
    public string ResolvedFolderPath => _parent.ResolveAgainstProject(Model.FolderPath);

    public bool CanPackage
    {
        get
        {
            if (IsPackaging || _parent.IsPackaging || !_parent.IsGameReady)
                return false;
            var folder = ResolvedFolderPath;
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        }
    }

    public void RaiseCanPackageChanged() => PackageThisCommand.RaiseCanExecuteChanged();

    private void GoToPackage()
    {
        if (string.IsNullOrEmpty(LastPackagePath))
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastPackagePath}\"")
            { UseShellExecute = true });
    }

    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose folder to package" };
        try
        {
            if (ResolvedFolderPath is { Length: > 0 } cur && Directory.Exists(cur))
                dlg.InitialDirectory = cur;
        }
        catch
        {
            // Ignore a malformed current path - open at the default location.
        }
        if (dlg.ShowDialog() == true)
        {
            FolderPath = _parent.MakeProjectRelative(dlg.FolderName);
            if (string.IsNullOrWhiteSpace(Name))
                Name = Path.GetFileName(dlg.FolderName.TrimEnd('\\', '/'));
        }
    }

    private void AddAsset()
    {
        var model = new PackageAsset();
        Assets.Add(new PackageAssetViewModel(this, model));
        OnPropertyChanged(nameof(AssetSummary));
        Save();
    }

    public void RemoveAsset(PackageAssetViewModel asset)
    {
        Assets.Remove(asset);
        OnPropertyChanged(nameof(AssetSummary));
        Save();
    }

    public void CloneAsset(PackageAssetViewModel asset)
    {
        var clone = asset.Model.Clone();
        var index = Assets.IndexOf(asset);
        var vm = new PackageAssetViewModel(this, clone);
        if (index >= 0)
            Assets.Insert(index + 1, vm);
        else
            Assets.Add(vm);
        OnPropertyChanged(nameof(AssetSummary));
        Save();
    }
}
