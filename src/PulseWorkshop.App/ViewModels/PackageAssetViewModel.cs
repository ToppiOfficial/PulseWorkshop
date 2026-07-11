using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;
using PulseWorkshop.Core.Models;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// One pre-asset row under a package entry: an input file plus how it is transformed and where it is
/// written inside the entry folder. Text assets edit a list of <see cref="RegexReplaceViewModel"/>;
/// image assets pick a target format. Edits write to the model and save the shared project via the
/// owning <see cref="PackageEntryViewModel"/>.
/// </summary>
public sealed class PackageAssetViewModel : ObservableObject
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".tga", ".dds", ".vtf", ".psd" };

    private readonly PackageEntryViewModel _entry;
    private AssetKindChoice _selectedKind;
    private ImageFormatChoice _selectedImageFormat;
    private ImageSource? _thumbnailSource;
    private bool _thumbnailIsFileIcon;

    public PackageAssetViewModel(PackageEntryViewModel entry, PackageAsset model)
    {
        _entry = entry;
        Model = model;

        _selectedKind = entry.AssetKinds.FirstOrDefault(k => k.Kind == model.Kind) ?? entry.AssetKinds[0];
        _selectedImageFormat = entry.ImageFormats.FirstOrDefault(f => f.Format == model.ImageFormat)
            ?? entry.ImageFormats[0];

        foreach (var r in model.RegexReplaces)
            Regexes.Add(new RegexReplaceViewModel(this, r));

        _thumbnailSource = LoadThumbnail();

        BrowseInputCommand = new RelayCommand(BrowseInput);
        OpenInputCommand = new RelayCommand(OpenInput, CanOpenInput);
        BrowseVmtTemplateCommand = new RelayCommand(BrowseVmtTemplate);
        RemoveCommand = new RelayCommand(() => _entry.RemoveAsset(this));
        CloneCommand = new RelayCommand(() => _entry.CloneAsset(this));
        AddRegexCommand = new RelayCommand(AddRegex);
    }

    public PackageAsset Model { get; }

    public IReadOnlyList<AssetKindChoice> AssetKinds => _entry.AssetKinds;
    public IReadOnlyList<ImageFormatChoice> ImageFormats => _entry.ImageFormats;

    /// <summary>The find/replace passes for a text asset.</summary>
    public ObservableCollection<RegexReplaceViewModel> Regexes { get; } = new();

    public RelayCommand BrowseInputCommand { get; }

    /// <summary>Opens the asset's input file in the OS default program. Holding Alt instead reveals it
    /// in Explorer (its folder, file selected). Mirrors the Compile - Advanced entry's "Open file".</summary>
    public RelayCommand OpenInputCommand { get; }

    public RelayCommand BrowseVmtTemplateCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand CloneCommand { get; }
    public RelayCommand AddRegexCommand { get; }

    /// <summary>Rebuilds the model's regex list from the UI collection (called before save / package).</summary>
    public void SyncRegex() => Model.RegexReplaces = Regexes.Select(r => r.Model).ToList();

    /// <summary>Saves the project via the owning entry (regex passes are synced first).</summary>
    public void Save()
    {
        SyncRegex();
        _entry.Save();
    }

    public AssetKindChoice SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (SetField(ref _selectedKind, value ?? AssetKinds[0]))
            {
                Model.Kind = _selectedKind.Kind;
                OnPropertyChanged(nameof(IsText));
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(IsVtf));
                OnPropertyChanged(nameof(ShowVmtTemplate));
                RefreshThumbnail();
                RefreshValidation();
                Save();
            }
        }
    }

    public bool IsText => Model.Kind == AssetKind.Text;
    public bool IsImage => Model.Kind == AssetKind.Image;

    /// <summary>Preview for the input file: the image itself when Kind=Image and the file decodes,
    /// the file's shell icon otherwise (text assets, or image formats WPF can't decode); null when
    /// the file is missing or the path is blank.</summary>
    public ImageSource? ThumbnailSource => _thumbnailSource;

    /// <summary>True when <see cref="ThumbnailSource"/> is the file-type shell icon rather than the
    /// image's own pixels - the view renders icons small and centered instead of filling the box.</summary>
    public bool ThumbnailIsFileIcon => _thumbnailIsFileIcon;

    /// <summary>True when the thumbnail shows the image's own pixels - clicking it then opens the
    /// full-size preview window.</summary>
    public bool HasImagePreview => _thumbnailSource is not null && !_thumbnailIsFileIcon;

    /// <summary>True when this is a text asset with an existing input file - clicking its thumbnail
    /// opens the raw text in a <see cref="TextPreviewWindow"/>.</summary>
    public bool HasTextPreview => IsText && ResolvedInputPath() is not null;

    private void RefreshThumbnail()
    {
        var next = LoadThumbnail();
        if (ReferenceEquals(_thumbnailSource, next)) return;
        _thumbnailSource = next;
        OnPropertyChanged(nameof(ThumbnailSource));
        OnPropertyChanged(nameof(ThumbnailIsFileIcon));
        OnPropertyChanged(nameof(HasImagePreview));
        OnPropertyChanged(nameof(HasTextPreview));
    }

    /// <summary>
    /// Re-checks the input file on disk: reloads the thumbnail when the file has appeared or
    /// disappeared since the last load, and re-raises validation. Called by the owning entry when
    /// the app regains focus, so a path typed before the file existed catches up without the user
    /// having to re-enter it.
    /// </summary>
    public void RefreshFileState()
    {
        if ((ResolvedInputPath() is not null) != (_thumbnailSource is not null))
            RefreshThumbnail();
        RefreshValidation();
        OpenInputCommand.RaiseCanExecuteChanged();
    }

    /// <summary>The input path resolved against the project when it points at an existing file;
    /// null otherwise (blank path, unresolvable, or missing file).</summary>
    public string? ResolvedInputPath()
    {
        if (string.IsNullOrWhiteSpace(Model.InputPath)) return null;
        var resolved = _entry.ResolveAgainst(Model.InputPath);
        return !string.IsNullOrEmpty(resolved) && File.Exists(resolved) ? resolved : null;
    }

    private ImageSource? LoadThumbnail()
    {
        _thumbnailIsFileIcon = false;
        if (string.IsNullOrWhiteSpace(Model.InputPath)) return null;
        var resolved = _entry.ResolveAgainst(Model.InputPath);
        if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved)) return null;

        if (IsImage)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(resolved, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 64;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                // Not decodable by WPF (e.g. .tga/.dds/.vtf) - fall through to the shell icon.
            }
        }

        var icon = ShellIcon.GetFileIcon(resolved);
        _thumbnailIsFileIcon = icon is not null;
        return icon;
    }

    public ImageFormatChoice SelectedImageFormat
    {
        get => _selectedImageFormat;
        set
        {
            if (SetField(ref _selectedImageFormat, value ?? ImageFormats[0]))
            {
                Model.ImageFormat = _selectedImageFormat.Format;
                OnPropertyChanged(nameof(IsVtf));
                OnPropertyChanged(nameof(ShowVmtTemplate));
                RefreshValidation();
                Save();
            }
        }
    }

    /// <summary>True when Kind=Image and the target format is VTF - drives the VTF cmd row's visibility.</summary>
    public bool IsVtf => Model.Kind == AssetKind.Image && Model.ImageFormat == ImageTargetFormat.Vtf;

    public string VtfCommand
    {
        get => Model.VtfCommand;
        set
        {
            if (Model.VtfCommand != (value ?? string.Empty))
            {
                Model.VtfCommand = value ?? string.Empty;
                OnPropertyChanged();
                Save();
            }
        }
    }

    /// <summary>When set (VTF only), a .vmt is written next to the produced VTF with its $basetexture
    /// pointed at that VTF. Drives the VMT template box's visibility.</summary>
    public bool CreateVmt
    {
        get => Model.CreateVmt;
        set
        {
            if (Model.CreateVmt != value)
            {
                Model.CreateVmt = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowVmtTemplate));
                Save();
            }
        }
    }

    /// <summary>Path to the base .vmt file; its $basetexture is rewritten to the produced VTF (or
    /// inserted if missing). Blank or missing generates a minimal VertexLitGeneric material.</summary>
    public string VmtTemplatePath
    {
        get => Model.VmtTemplatePath;
        set
        {
            if (Model.VmtTemplatePath != (value ?? string.Empty))
            {
                Model.VmtTemplatePath = value ?? string.Empty;
                OnPropertyChanged();
                Save();
            }
        }
    }

    /// <summary>True when the VMT template box should show (VTF format with "Create VMT" ticked).</summary>
    public bool ShowVmtTemplate => IsVtf && Model.CreateVmt;

    public string InputPath
    {
        get => Model.InputPath;
        set
        {
            if (Model.InputPath != (value ?? string.Empty))
            {
                Model.InputPath = value ?? string.Empty;
                OnPropertyChanged();
                RefreshThumbnail();
                RefreshValidation();
                OpenInputCommand.RaiseCanExecuteChanged();
                Save();
            }
        }
    }

    public string OutputDir
    {
        get => Model.OutputDir;
        set
        {
            if (Model.OutputDir != (value ?? string.Empty))
            {
                Model.OutputDir = value ?? string.Empty;
                OnPropertyChanged();
                RefreshValidation();
                Save();
            }
        }
    }

    public string OutputFileName
    {
        get => Model.OutputFileName;
        set
        {
            if (Model.OutputFileName != (value ?? string.Empty))
            {
                Model.OutputFileName = value ?? string.Empty;
                OnPropertyChanged();
                RefreshValidation();
                Save();
            }
        }
    }

    /// <summary>Why this asset would fail (or be skipped) if packaged right now, or null when it's
    /// clean. Mirrors <c>AssetPipelineService.ApplyAsync</c>'s skip conditions so the UI can flag
    /// problems live instead of only after a failed package run.</summary>
    public string? ValidationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(InputPath))
                return "No input file set.";

            var resolvedInput = _entry.ResolveAgainst(InputPath);
            if (string.IsNullOrEmpty(resolvedInput) || !File.Exists(resolvedInput))
                return "Input file not found.";

            // PSD is only handled by the VTF tool (vtfcmd reads .psd); WPF can't decode it for a raster
            // re-encode, and copying a .psd into a package is useless to the engine.
            if (IsImage && IsPsd(resolvedInput) && Model.ImageFormat != ImageTargetFormat.Vtf)
                return "PSD input is only supported for VTF conversion.";

            var root = _entry.ResolvedFolderPath;
            if (string.IsNullOrWhiteSpace(root))
                return null; // The entry's own folder is invalid; its outline already flags that.

            var destDir = AssetPipelineService.SandboxedDir(root, Model.OutputDir);
            if (destDir is null)
                return "Output dir escapes the package folder.";

            if (IsVtf && !AssetPipelineService.IsMaterialsRooted(Model.OutputDir))
                return "VTF output dir must start with 'materials' (Source engine requires vtf/vmt under materials/).";

            var fileName = string.IsNullOrWhiteSpace(OutputFileName) ? Path.GetFileName(resolvedInput) : OutputFileName;
            var dest = Path.Combine(destDir, fileName);
            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(resolvedInput), StringComparison.OrdinalIgnoreCase))
                return "Output would overwrite the source file.";

            return null;
        }
    }

    /// <summary>True when the given path is a Photoshop document (only convertible via the VTF tool).</summary>
    private static bool IsPsd(string path) =>
        string.Equals(Path.GetExtension(path), ".psd", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this asset has a live validation error - drives the red outline.</summary>
    public bool IsInvalid => ValidationError is not null;

    /// <summary>Re-raises <see cref="ValidationError"/>/<see cref="IsInvalid"/> change notifications.
    /// Called on any edit that affects the outcome, and by the owning entry when its folder path
    /// changes (the sandbox/materials checks are resolved against that folder).</summary>
    public void RefreshValidation()
    {
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(IsInvalid));
    }

    private bool CanOpenInput() => ResolvedInputPath() is not null;

    private void OpenInput()
    {
        if (ResolvedInputPath() is not { } path) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void BrowseInput()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose asset file",
            Filter = "Image (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff;*.tga;*.dds;*.vtf;*.psd)"
                   + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff;*.tga;*.dds;*.vtf;*.psd"
                   + "|Text (*.txt;*.vmt;*.vdf;*.qc;*.smd;*.cfg;*.lua)|*.txt;*.vmt;*.vdf;*.qc;*.smd;*.cfg;*.lua"
                   + "|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        try
        {
            var cur = _entry.ResolveAgainst(Model.InputPath);
            if (!string.IsNullOrEmpty(cur) && Path.GetDirectoryName(cur) is { Length: > 0 } dir
                && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch
        {
            // Ignore a malformed current path.
        }
        if (dlg.ShowDialog() != true)
            return;

        InputPath = _entry.MakeRelative(dlg.FileName);

        // Default the kind from the extension and fill an output name if blank.
        var ext = Path.GetExtension(dlg.FileName);
        if (ImageExtensions.Contains(ext) && AssetKinds.FirstOrDefault(k => k.Kind == AssetKind.Image) is { } imgKind)
            SelectedKind = imgKind;
        if (string.IsNullOrWhiteSpace(OutputFileName))
            OutputFileName = Path.GetFileName(dlg.FileName);
    }

    private void BrowseVmtTemplate()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose VMT template",
            Filter = "Material (*.vmt)|*.vmt|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        try
        {
            var cur = _entry.ResolveAgainst(Model.VmtTemplatePath);
            if (!string.IsNullOrEmpty(cur) && Path.GetDirectoryName(cur) is { Length: > 0 } dir
                && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch
        {
            // Ignore a malformed current path.
        }
        if (dlg.ShowDialog() != true)
            return;

        VmtTemplatePath = _entry.MakeRelative(dlg.FileName);
    }

    private void AddRegex()
    {
        var model = new RegexReplace();
        Regexes.Add(new RegexReplaceViewModel(this, model));
        Save();
    }

    public void RemoveRegex(RegexReplaceViewModel regex)
    {
        Regexes.Remove(regex);
        Save();
    }
}

/// <summary>One find/replace pass under a text asset.</summary>
public sealed class RegexReplaceViewModel : ObservableObject
{
    private readonly PackageAssetViewModel _asset;

    public RegexReplaceViewModel(PackageAssetViewModel asset, RegexReplace model)
    {
        _asset = asset;
        Model = model;
        RemoveCommand = new RelayCommand(() => _asset.RemoveRegex(this));
    }

    public RegexReplace Model { get; }
    public RelayCommand RemoveCommand { get; }

    public string Pattern
    {
        get => Model.Pattern;
        set { if (Model.Pattern != (value ?? string.Empty)) { Model.Pattern = value ?? string.Empty; OnPropertyChanged(); _asset.Save(); } }
    }

    public string Replacement
    {
        get => Model.Replacement;
        set { if (Model.Replacement != (value ?? string.Empty)) { Model.Replacement = value ?? string.Empty; OnPropertyChanged(); _asset.Save(); } }
    }

    public bool IgnoreCase
    {
        get => Model.IgnoreCase;
        set { if (Model.IgnoreCase != value) { Model.IgnoreCase = value; OnPropertyChanged(); _asset.Save(); } }
    }

    public bool Multiline
    {
        get => Model.Multiline;
        set { if (Model.Multiline != value) { Model.Multiline = value; OnPropertyChanged(); _asset.Save(); } }
    }

    public bool IsLiteral
    {
        get => Model.IsLiteral;
        set { if (Model.IsLiteral != value) { Model.IsLiteral = value; OnPropertyChanged(); _asset.Save(); } }
    }
}
