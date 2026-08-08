using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.App.Services;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Services;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// One row in the Advanced compile project's entries list: wraps a <see cref="ModelEntry"/> and
/// defers persistence + compilation to its owning <see cref="CompileAdvancedViewModel"/>. Editing any
/// field writes straight to the model and asks the parent to save the <c>.pw_mdlproject</c>.
/// </summary>
public sealed class ModelEntryViewModel : ObservableObject
{
    private readonly CompileAdvancedViewModel _parent;
    private bool _isCompiling;
    private bool _isSelected;
    private bool _hasError;
    private string? _lastMdlPath;
    private string? _mdlInfo;
    private MaterialDirEntry? _selectedMaterialDir;

    public ModelEntryViewModel(CompileAdvancedViewModel parent, ModelEntry model)
    {
        _parent = parent;
        Model = model;
        _selectedOutputMode = parent.OutputModes.FirstOrDefault(o => o.Mode == model.OutputMode)
            ?? parent.OutputModes[0];

        BrowseQcCommand = new RelayCommand(BrowseQc);
        CloneCommand = new RelayCommand(() => _parent.CloneEntry(this));
        RemoveCommand = new RelayCommand(() => _parent.RemoveEntry(this));
        GoToMdlCommand = new RelayCommand(GoToMdl, () => !string.IsNullOrEmpty(LastMdlPath));
        ViewModelCommand = new RelayCommand(
            () => { if (!string.IsNullOrEmpty(LastMdlPath)) _parent.RequestViewModel(LastMdlPath); },
            () => !string.IsNullOrEmpty(LastMdlPath));
        OpenQcCommand = new RelayCommand(OpenQc, CanOpenQc);
        GoToMaterialsCommand = new RelayCommand(GoToMaterials, () => HasMaterialDirs);
    }

    public ModelEntry Model { get; }

    /// <summary>The output-destination choices, shared with the parent so ComboBox selection matches.</summary>
    public IReadOnlyList<OutputModeChoice> OutputModes => _parent.OutputModes;

    public RelayCommand BrowseQcCommand { get; }
    public RelayCommand CloneCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand GoToMdlCommand { get; }

    /// <summary>Sends this entry's last compiled .mdl to the Model View tab (without launching the viewer).</summary>
    public RelayCommand ViewModelCommand { get; }

    /// <summary>Opens this entry's .qc in the OS default editor (whatever is associated with .qc).
    /// Holding Alt instead reveals the .qc in Explorer (its folder, file selected).</summary>
    public RelayCommand OpenQcCommand { get; }

    private bool CanOpenQc()
    {
        var qc = ResolvedQcPath;
        return !string.IsNullOrWhiteSpace(qc) && File.Exists(qc);
    }

    private void OpenQc()
    {
        if (!CanOpenQc()) return;
        ShellOpen.Open(ResolvedQcPath);
    }

    /// <summary>The .mdl from this entry's last successful compile (drives its own "Go to file").</summary>
    public string? LastMdlPath
    {
        get => _lastMdlPath;
        set
        {
            if (SetField(ref _lastMdlPath, value))
            {
                GoToMdlCommand.RaiseCanExecuteChanged();
                ViewModelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void GoToMdl()
    {
        if (string.IsNullOrEmpty(LastMdlPath))
            return;
        ShellOpen.Reveal(LastMdlPath);
    }

    /// <summary>The .mdl summary (bones, hitboxes, poly count, dependencies) from this entry's last
    /// successful compile - read by ModelTool and shown read-only in the editor. Not persisted.</summary>
    public string? MdlInfo
    {
        get => _mdlInfo;
        set => SetField(ref _mdlInfo, value);
    }

    /// <summary>Opens the model's material folder (created by "Make material's directory" after an
    /// in-game compile) in Explorer. When the model has several, the dropdown beside it picks which.</summary>
    public RelayCommand GoToMaterialsCommand { get; }

    /// <summary>The model's material folders created under the game's <c>materials/</c> on the last
    /// in-game compile with "Make material's directory" on. Empty otherwise. Not persisted.</summary>
    public ObservableCollection<MaterialDirEntry> MaterialDirs { get; } = new();

    /// <summary>Which material folder "Go to Materials" opens (bound to the dropdown). Defaults to the
    /// first when the folders are set.</summary>
    public MaterialDirEntry? SelectedMaterialDir
    {
        get => _selectedMaterialDir;
        set => SetField(ref _selectedMaterialDir, value);
    }

    /// <summary>True when the last in-game compile created at least one material folder.</summary>
    public bool HasMaterialDirs => MaterialDirs.Count > 0;

    /// <summary>True when there is more than one material folder, so the picker dropdown is shown.</summary>
    public bool HasMultipleMaterialDirs => MaterialDirs.Count > 1;

    /// <summary>Replaces the entry's material folders (called after the compile creates them on disk),
    /// selecting the first and refreshing the "Go to Materials" button + dropdown.</summary>
    public void SetMaterialDirs(IReadOnlyList<MaterialDirEntry> dirs)
    {
        MaterialDirs.Clear();
        foreach (var d in dirs)
            MaterialDirs.Add(d);
        SelectedMaterialDir = MaterialDirs.FirstOrDefault();
        OnPropertyChanged(nameof(HasMaterialDirs));
        OnPropertyChanged(nameof(HasMultipleMaterialDirs));
        GoToMaterialsCommand.RaiseCanExecuteChanged();
    }

    private void GoToMaterials()
    {
        var target = SelectedMaterialDir ?? MaterialDirs.FirstOrDefault();
        if (target is null || !Directory.Exists(target.FullPath))
            return;
        ShellOpen.Reveal(target.FullPath);
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

    public string QcPath
    {
        get => Model.QcPath;
        set
        {
            if (Model.QcPath != (value ?? string.Empty))
            {
                Model.QcPath = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(QcSummary));
                OnPropertyChanged(nameof(CommandPreview));
                OpenQcCommand.RaiseCanExecuteChanged();
                _parent.Save();
                _parent.RefreshCommands();
                _parent.OnEntryQcChanged(this);
            }
        }
    }

    /// <summary>The .qc file shown in the entries sidebar (the file name, or "No QC file").</summary>
    public string QcSummary =>
        string.IsNullOrWhiteSpace(Model.QcPath) ? "No QC file" : Path.GetFileName(Model.QcPath);

    public bool CompileInAll
    {
        get => Model.CompileInAll;
        set
        {
            if (Model.CompileInAll != value)
            {
                Model.CompileInAll = value;
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
                OnPropertyChanged(nameof(CommandPreview));
                _parent.Save();
            }
        }
    }

    /// <summary>Read-only studiomdl command-line preview (the project's global command plus this
    /// entry's command), mirroring the Simple tab. Built by the parent so it reflects the selected
    /// game and gameinfo.txt; refreshed when this entry's QC/command or those project inputs change.</summary>
    public string CommandPreview => _parent.BuildEntryCommandPreview(this);

    /// <summary>Lets the parent re-raise this entry's preview when a project-level input changes.</summary>
    public void RaiseCommandPreviewChanged() => OnPropertyChanged(nameof(CommandPreview));

    private OutputModeChoice _selectedOutputMode;
    public OutputModeChoice SelectedOutputMode
    {
        get => _selectedOutputMode;
        set
        {
            if (SetField(ref _selectedOutputMode, value ?? OutputModes[0]))
            {
                Model.OutputMode = _selectedOutputMode.Mode;
                OnPropertyChanged(nameof(ShowSubfolder));
                OnPropertyChanged(nameof(OutputModeLabel));
                OnPropertyChanged(nameof(OutputModeTooltip));
                _parent.Save();
                _parent.RefreshCommands();
            }
        }
    }

    public string SubfolderName
    {
        get => Model.SubfolderName;
        set
        {
            if (Model.SubfolderName != (value ?? string.Empty))
            {
                Model.SubfolderName = value ?? string.Empty;
                OnPropertyChanged();
                _parent.Save();
                _parent.RefreshCommands();
            }
        }
    }

    public bool ShowSubfolder => Model.OutputMode == CompileOutputMode.Subfolder;

    /// <summary>Short badge text for the entries list: which of the two output destinations this
    /// entry compiles to.</summary>
    public string OutputModeLabel => Model.OutputMode switch
    {
        CompileOutputMode.Subfolder => "Subfolder",
        CompileOutputMode.LeaveInGame => "In-game",
        _ => "Custom",
    };

    public string OutputModeTooltip => Model.OutputMode switch
    {
        CompileOutputMode.Subfolder => "Compiles to a subfolder under the project.",
        CompileOutputMode.LeaveInGame => "Leaves the compiled files where studiomdl wrote them, in the game's models folder.",
        _ => "Custom output destination.",
    };

    /// <summary>Per-entry busy flag (true while this single entry, or a "compile all" pass, runs).
    /// Drives the blue "compiling" outline.</summary>
    public bool IsCompiling
    {
        get => _isCompiling;
        set => SetField(ref _isCompiling, value);
    }

    /// <summary>True when this row is highlighted in the entries list. Drives "Compile selected";
    /// bound two-way to the ListBoxItem so Ctrl/Shift multi-selection flows into the view model.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
                _parent.OnEntrySelectionChanged();
        }
    }

    /// <summary>True when this entry's last compile failed (no .mdl / studiomdl error). Drives the red
    /// outline; it persists until the next compile of this entry (or an app restart - it isn't saved).</summary>
    public bool HasError
    {
        get => _hasError;
        set => SetField(ref _hasError, value);
    }

    /// <summary>The .qc resolved to an absolute path against the project folder.</summary>
    public string ResolvedQcPath => _parent.ResolveAgainstProject(Model.QcPath);

    private void BrowseQc()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose QC file",
            Filter = QcFile.DialogFilter,
            CheckFileExists = true,
        };
        try
        {
            if (ResolvedQcPath is { Length: > 0 } cur && Path.GetDirectoryName(cur) is { Length: > 0 } dir
                && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch
        {
            // Ignore a malformed current path - open at the default location.
        }
        if (dlg.ShowDialog() == true)
        {
            // Store relative to the project when the .qc sits under it; otherwise keep it absolute.
            QcPath = _parent.MakeProjectRelative(dlg.FileName);
            if (string.IsNullOrWhiteSpace(Name))
                Name = Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }
}

/// <summary>One material folder created for a model: its path relative to <c>materials/</c> (shown
/// in the picker dropdown) plus the absolute folder to open. The themed ComboBox renders ToString.</summary>
public sealed record MaterialDirEntry(string RelativeDir, string FullPath)
{
    public override string ToString() => RelativeDir;
}
