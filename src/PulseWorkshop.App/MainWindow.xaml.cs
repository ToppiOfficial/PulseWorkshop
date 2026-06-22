using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using PulseWorkshop.App.ViewModels;
using PulseWorkshop.Core.Models;
using PulseWorkshop.Core.Storage;

namespace PulseWorkshop.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    // Persisted UI preferences (console open state + height), loaded on startup, saved on close.
    private readonly UiSettings _settings = UiSettings.Load();

    // Remembered console drawer height (px) so toggling hide/show restores the user's drag size.
    private double _consoleHeightPx = 180;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Title = $"PulseWorkshop - Steam Workshop Manager (v{AppVersion})";
        _vm.NavigateToDrafts += () => DraftsTab.IsSelected = true;
        _vm.NavigateToTemplates += () => TemplatesTab.IsSelected = true;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.ConsoleLines.CollectionChanged += OnConsoleLinesChanged;

        // Restore the console drawer state. Height must be set before IsConsoleVisible so the toggle
        // expands the rows to the remembered size.
        if (_settings.ConsoleHeight > 0)
            _consoleHeightPx = _settings.ConsoleHeight;
        _vm.IsConsoleVisible = _settings.ConsoleVisible;

        Closed += async (_, _) =>
        {
            SaveUiSettings();
            await _vm.DisposeAsync();
        };
    }

    private void SaveUiSettings()
    {
        if (_vm.IsConsoleVisible && ConsoleRowDef.ActualHeight > 1)
            _consoleHeightPx = ConsoleRowDef.ActualHeight;
        _settings.ConsoleVisible = _vm.IsConsoleVisible;
        _settings.ConsoleHeight = _consoleHeightPx;
        _settings.Save();
    }

    // --- Console drawer ------------------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsConsoleVisible))
            UpdateConsoleRowHeights();
    }

    /// <summary>
    /// Expands/collapses the console rows. RowDefinition.Height can't bind cleanly to a bool, so the
    /// toggle is driven here; the dragged height is remembered across hide/show.
    /// </summary>
    private void UpdateConsoleRowHeights()
    {
        if (_vm.IsConsoleVisible)
        {
            ConsoleSplitterRow.Height = new GridLength(6);
            ConsoleRowDef.Height = new GridLength(_consoleHeightPx);
            ConsoleRowDef.MinHeight = 80;
            ScrollConsoleToEnd();
        }
        else
        {
            // Preserve the current (possibly dragged) height before collapsing.
            if (ConsoleRowDef.ActualHeight > 1)
                _consoleHeightPx = ConsoleRowDef.ActualHeight;
            ConsoleRowDef.MinHeight = 0;
            ConsoleRowDef.Height = new GridLength(0);
            ConsoleSplitterRow.Height = new GridLength(0);
        }
    }

    private void OnConsoleLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add)
            ScrollConsoleToEnd();
    }

    private void ScrollConsoleToEnd()
    {
        // Scroll the ListBox's own ScrollViewer rather than ScrollIntoView - log lines can repeat,
        // and ScrollIntoView would jump to the first equal item instead of the newest one.
        if (FindScrollViewer(ConsoleList) is { } sv)
            sv.ScrollToEnd();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
            return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Darken the OS title bar to match the app's dark-grey theme. WPF doesn't theme the
    /// non-client area, so we ask DWM to use the immersive dark title bar once the HWND exists.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkTitleBar();
    }

    // DWM window attribute that toggles the dark (immersive) title bar on Windows 10 20H1+ / 11.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int useDark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
    }

    /// <summary>The app version (from the assembly's <see cref="Version"/> in the .csproj), e.g. "0.1.0".</summary>
    private static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>
    /// Enter in a search box flushes the pending (debounced) filter immediately instead of waiting
    /// for the typing-pause timer. The TextBox is matched to its list by x:Name.
    /// </summary>
    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        switch ((sender as FrameworkElement)?.Name)
        {
            case nameof(PublishedSearchBox): _vm.ApplyPublishedSearchNow(); break;
            case nameof(DraftsSearchBox): _vm.ApplyDraftsSearchNow(); break;
            case nameof(TemplatesSearchBox): _vm.ApplyTemplatesSearchNow(); break;
        }
    }

    private void PublishedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm.IsBusy)
            return;
        if (((ListBox)sender).SelectedItem is WorkshopItem item)
        {
            ClearOtherSelections(PublishedList);
            _vm.EditPublished(item);
        }
    }

    private void DraftsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm.IsBusy)
            return;
        if (((ListBox)sender).SelectedItem is DraftListItemViewModel item)
        {
            ClearOtherSelections(DraftsList);
            _vm.EditDraft(item.Draft);
        }
    }

    /// <summary>
    /// Opens the Workshop item in the Steam client (steam://) so it appears in the Steam overlay/
    /// community; if Steam can't handle the protocol, falls back to the default browser.
    /// </summary>
    private void OpenWorkshopItem_Click(object sender, RoutedEventArgs e)
    {
        if (RowData<WorkshopItem>(sender) is not { } item)
            return;

        var steamUrl = $"steam://url/CommunityFilePage/{item.PublishedFileId}";
        if (!TryOpen(steamUrl))
            TryOpen(item.WorkshopUrl); // browser fallback
    }

    /// <summary>
    /// Opens the official Steam "Add/Edit Images &amp; Videos" page for the item being edited.
    /// Gallery previews have no SDK API for these legacy Workshop games, so we deep-link instead.
    /// </summary>
    private void OpenManagePreviews_Click(object sender, RoutedEventArgs e) =>
        OpenSteamItemPage("https://steamcommunity.com/sharedfiles/managepreviews/?id=");

    /// <summary>Opens the official Steam "Manage Required Items" page for the item being edited.</summary>
    private void OpenManageRequiredItems_Click(object sender, RoutedEventArgs e) =>
        OpenSteamItemPage("https://steamcommunity.com/workshop/managerequireditems/?id=");

    /// <summary>
    /// Deep-links to a Steam Workshop management page for the currently-edited published item,
    /// preferring the Steam client (steam://openurl) and falling back to the default browser.
    /// </summary>
    private void OpenSteamItemPage(string baseUrl)
    {
        if (_vm.Editor?.PublishedFileId is not { } id)
            return;

        var webUrl = baseUrl + id;
        if (!TryOpen("steam://openurl/" + webUrl))
            TryOpen(webUrl);
    }

    /// <summary>Launches a URL via the shell's default handler; returns false if it couldn't start.</summary>
    private static bool TryOpen(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TemplatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm.IsBusy)
            return;
        if (((ListBox)sender).SelectedItem is TemplateListItemViewModel item)
        {
            ClearOtherSelections(TemplatesList);
            _vm.EditTemplate(item.Template);
        }
    }

    /// <summary>
    /// The three lists (Published / Drafts / Templates) each track their own selection. Opening an
    /// item from one list must clear the highlight in the other two - otherwise a stale selection
    /// lingers, and re-clicking that still-selected row is a no-op (SelectionChanged never fires),
    /// leaving the editor (and its template-vs-draft buttons) showing the wrong mode.
    /// </summary>
    private void ClearOtherSelections(ListBox keep)
    {
        foreach (var list in new[] { PublishedList, DraftsList, TemplatesList })
        {
            if (!ReferenceEquals(list, keep))
                list.SelectedItem = null;
        }
    }

    // --- Context menu / template handlers ------------------------------------------------------

    private static T? RowData<T>(object sender) where T : class
    {
        // A clicked element inside a list row carries that row's data item as its DataContext.
        if (sender is FrameworkElement { DataContext: T data })
            return data;
        return null;
    }

    private void DraftDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RowData<DraftListItemViewModel>(sender) is not { } item)
            return;

        var isLinked = item.Draft.Edit.PublishedFileId is not null;
        var message = isLinked
            ? $"Delete the in-progress edit draft \"{item.Draft.Name}\"?\n\n" +
              "This discards your unsaved edits and reverts to the published item. The Workshop item itself is NOT deleted."
            : $"Delete draft \"{item.Draft.Name}\"?";

        if (Confirm("Delete draft", message))
            _vm.DeleteDraft(item.Draft);
    }

    private void DraftClone_Click(object sender, RoutedEventArgs e)
    {
        if (RowData<DraftListItemViewModel>(sender) is { } item)
            _vm.CloneDraft(item.Draft);
    }

    private void TemplateUse_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy || RowData<TemplateListItemViewModel>(sender) is not { } item)
            return;

        _vm.ApplyTemplate(item.Template, out var removed);
        if (removed.Count > 0)
        {
            MessageBox.Show(this,
                "Some files referenced by this template no longer exist and were removed from it:\n\n - " +
                string.Join("\n - ", removed),
                "Template files missing", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void TemplateDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RowData<TemplateListItemViewModel>(sender) is { } item &&
            Confirm("Delete template", $"Delete template \"{item.Template.Name}\"?"))
        {
            _vm.DeleteTemplate(item.Template);
        }
    }

    private bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            == MessageBoxResult.Yes;

    private void SaveAsTemplate_Click(object sender, RoutedEventArgs e) => _vm.SaveAsTemplate();

    // --- Content file zone ---------------------------------------------------------------------

    private EditorViewModel? Editor => _vm.Editor;

    private void ContentDrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (Editor is null)
            return;

        var ext = Editor.ContentFileExtension; // e.g. ".vpk"
        var dialog = new OpenFileDialog
        {
            Title = "Choose content file",
            Filter = $"Content file (*{ext})|*{ext}|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
            Editor.ContentFile = dialog.FileName;
    }

    private void ContentDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsSingleFileDrop(e, Editor?.ContentFileExtension) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ContentDrop_Drop(object sender, DragEventArgs e)
    {
        if (Editor is null)
            return;
        var path = GetDroppedFile(e, Editor.ContentFileExtension);
        if (path is not null)
            Editor.ContentFile = path;
    }

    // --- Preview image zone --------------------------------------------------------------------

    private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif" };

    private void PreviewDrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (Editor is null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Choose preview image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.gif)|*.png;*.jpg;*.jpeg;*.gif|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
            Editor.PreviewImagePath = dialog.FileName;
    }

    private void PreviewDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsSingleFileDrop(e, ImageExts) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void PreviewDrop_Drop(object sender, DragEventArgs e)
    {
        if (Editor is null)
            return;
        var path = GetDroppedFile(e, ImageExts);
        if (path is not null)
            Editor.PreviewImagePath = path;
    }

    // --- Drag-drop helpers ---------------------------------------------------------------------

    private static bool IsSingleFileDrop(DragEventArgs e, params string?[] allowedExts) =>
        GetDroppedFile(e, allowedExts) is not null;

    private static string? GetDroppedFile(DragEventArgs e, params string?[] allowedExts)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1)
            return null;

        var file = files[0];
        if (!File.Exists(file))
            return null;

        // No filter, or extension matches one of the allowed (case-insensitive).
        var exts = allowedExts.Where(x => !string.IsNullOrEmpty(x)).ToArray();
        if (exts.Length == 0)
            return file;

        var fileExt = Path.GetExtension(file);
        return exts.Any(x => string.Equals(x, fileExt, StringComparison.OrdinalIgnoreCase)) ? file : null;
    }
}
