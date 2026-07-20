using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PulseWorkshop.App.Mvvm;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Services;

namespace PulseWorkshop.App.ViewModels;

/// <summary>
/// One row in the Textures groups list: wraps a <see cref="TextureGroup"/> (a pattern + output rule) and
/// defers persistence + conversion to its owning <see cref="TexturesViewModel"/>. Editing any field writes
/// to the model and auto-saves the <c>.pw_textureproject</c>.
/// </summary>
public sealed class TextureGroupViewModel : ObservableObject
{
    private readonly TexturesViewModel _parent;
    private bool _isConverting;
    private bool _isSelected;
    private bool _hasError;

    public TextureGroupViewModel(TexturesViewModel parent, TextureGroup model)
    {
        _parent = parent;
        Model = model;

        CloneCommand = new RelayCommand(() => _parent.CloneGroup(this));
        RemoveCommand = new RelayCommand(() => _parent.RemoveGroup(this));
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
    }

    public TextureGroup Model { get; }

    public RelayCommand CloneCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }

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

    public string InputPattern
    {
        get => Model.InputPattern;
        set
        {
            if (Model.InputPattern != (value ?? string.Empty))
            {
                Model.InputPattern = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PatternSummary));
                RefreshValidation();
                _parent.Save();
                _parent.RequestMatchRefresh(this);
            }
        }
    }

    public bool IsRegex
    {
        get => Model.IsRegex;
        set
        {
            if (Model.IsRegex != value)
            {
                Model.IsRegex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PatternSummary));
                RefreshValidation();
                _parent.Save();
                _parent.RequestMatchRefresh(this);
            }
        }
    }

    public bool Recursive
    {
        get => Model.Recursive;
        set
        {
            if (Model.Recursive != value)
            {
                Model.Recursive = value;
                OnPropertyChanged();
                _parent.Save();
                _parent.RequestMatchRefresh(this);
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
                _parent.Save();
            }
        }
    }

    public string VtfCommand
    {
        get => Model.VtfCommand;
        set
        {
            if (Model.VtfCommand != (value ?? string.Empty))
            {
                Model.VtfCommand = value ?? string.Empty;
                OnPropertyChanged();
                _parent.Save();
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

    /// <summary>Short line shown under the name in the groups sidebar (e.g. "regex: \.png$").</summary>
    public string PatternSummary =>
        string.IsNullOrWhiteSpace(Model.InputPattern)
            ? "(no pattern)"
            : $"{(Model.IsRegex ? "regex" : "text")}: {Model.InputPattern}";

    /// <summary>Per-group busy flag (drives the blue "converting" state).</summary>
    public bool IsConverting
    {
        get => _isConverting;
        set => SetField(ref _isConverting, value);
    }

    /// <summary>True when this row is highlighted in the groups list. Drives "Convert selected"; bound
    /// two-way to the ListBoxItem so Ctrl/Shift multi-selection flows into the view model.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetField(ref _isSelected, value)) _parent.OnGroupSelectionChanged(); }
    }

    /// <summary>True when this group's last run failed, or its regex is invalid (drives the red outline).</summary>
    public bool HasError
    {
        get => _hasError || IsInvalid;
        set { if (SetField(ref _hasError, value)) OnPropertyChanged(); }
    }

    /// <summary>True when <see cref="IsRegex"/> is set but the pattern doesn't compile.</summary>
    public bool IsInvalid => !string.IsNullOrEmpty(ValidationError);

    /// <summary>The regex-compile error for an invalid pattern (tooltip), or null when the pattern is ok.</summary>
    public string? ValidationError
    {
        get
        {
            if (!Model.IsRegex || string.IsNullOrWhiteSpace(Model.InputPattern))
                return null;
            try
            {
                _ = new Regex(Model.InputPattern);
                return null;
            }
            catch (Exception ex)
            {
                return $"Invalid regex: {ex.Message}";
            }
        }
    }

    // --- Match preview ----------------------------------------------------------------------------

    /// <summary>Cap on previewed tiles - a broad pattern over a big tree can match thousands of files,
    /// and nobody scrolls that far. The count line still reports the true total.</summary>
    private const int MaxPreviewed = 300;

    private CancellationTokenSource? _scanSource;
    private bool _isScanning;
    private int _totalMatched;
    private int _claimedEarlier;

    /// <summary>The source files this group's pattern currently matches (capped at
    /// <see cref="MaxPreviewed"/>), shown as a thumbnail grid under the editor.</summary>
    public ObservableCollection<TextureMatchViewModel> Matches { get; } = new();

    /// <summary>True while a scan is running (the preview header shows a spinner-ish "Scanning...").</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set => SetField(ref _isScanning, value);
    }

    /// <summary>The count/size line above the grid.</summary>
    public string MatchSummary
    {
        get
        {
            if (IsScanning)
                return "Scanning...";

            var claimed = _claimedEarlier > 0 ? $" ({_claimedEarlier} claimed by an earlier group)" : string.Empty;
            if (_totalMatched == 0)
                return "No files match this pattern." + claimed;

            var bytes = Matches.Where(m => m.Bytes > 0).Sum(m => m.Bytes);
            var shown = _totalMatched > Matches.Count
                ? $"{_totalMatched} files (showing first {Matches.Count})"
                : $"{_totalMatched} file{(_totalMatched == 1 ? "" : "s")}";
            var outdated = Matches.Count(m => m.IsOutOfDate);
            var pending = outdated > 0 ? $" - {outdated} need converting" : " - all up to date";
            return $"{shown} - {TextureMatchViewModel.FormatSize(bytes)}{pending}{claimed}";
        }
    }

    /// <summary>True when the grid has nothing to show (drives the empty-state line).</summary>
    public bool HasMatches => Matches.Count > 0;

    /// <summary>
    /// Rescans the source folder for this group's matches and reloads their thumbnails. Cancels any scan
    /// already in flight, so rapid pattern edits only ever leave the newest result standing.
    /// </summary>
    public async Task RefreshMatchesAsync()
    {
        _scanSource?.Cancel();
        var source = _scanSource = new CancellationTokenSource();
        var ct = source.Token;

        var root = _parent.ResolvedSourceFolder;
        var earlier = _parent.GroupsBefore(this);
        IsScanning = true;
        OnPropertyChanged(nameof(MatchSummary));
        try
        {
            // Enumerate + stat off the UI thread: a recursive scan of a big materials tree isn't instant.
            var found = await Task.Run(() =>
            {
                var paths = TextureConversionService.FindGroupMatches(root, Model);
                var mine = TextureConversionService.RemoveClaimed(root, paths, earlier);
                var tiles = mine.Take(MaxPreviewed)
                    .Select(p => new TextureMatchViewModel(
                        p, root, TextureConversionService.IsOutOfDate(root, p, Model)))
                    .ToList();
                return (Total: mine.Count, Claimed: paths.Count - mine.Count, Tiles: tiles);
            }, ct);

            if (ct.IsCancellationRequested)
                return;

            _totalMatched = found.Total;
            _claimedEarlier = found.Claimed;
            Matches.Clear();
            foreach (var tile in found.Tiles)
                Matches.Add(tile);

            IsScanning = false;
            OnPropertyChanged(nameof(MatchSummary));
            OnPropertyChanged(nameof(HasMatches));

            // Decode thumbnails one at a time so the grid paints immediately and stays responsive.
            foreach (var tile in found.Tiles)
            {
                if (ct.IsCancellationRequested)
                    return;
                await tile.LoadThumbnailAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer scan superseded this one.
        }
        finally
        {
            if (ReferenceEquals(_scanSource, source))
            {
                IsScanning = false;
                OnPropertyChanged(nameof(MatchSummary));
                _scanSource = null;
            }
            source.Dispose();
        }
    }

    private void RefreshValidation()
    {
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(IsInvalid));
        OnPropertyChanged(nameof(HasError));
    }

    private void BrowseOutput()
    {
        var dlg = new OpenFolderDialog { Title = "Choose output folder (under the source folder)" };
        try
        {
            var source = _parent.ResolvedSourceFolder;
            if (!string.IsNullOrEmpty(source) && Directory.Exists(source))
                dlg.InitialDirectory = source;
        }
        catch
        {
            // Ignore a malformed path - open at the default location.
        }
        if (dlg.ShowDialog() != true)
            return;

        // Store the pick relative to the source folder (blank = the source root itself).
        var source2 = _parent.ResolvedSourceFolder;
        if (!string.IsNullOrEmpty(source2))
        {
            try
            {
                var rel = Path.GetRelativePath(source2, dlg.FolderName);
                OutputDir = rel is "." ? string.Empty : rel;
                return;
            }
            catch
            {
                // Fall through to the absolute pick.
            }
        }
        OutputDir = dlg.FolderName;
    }
}
