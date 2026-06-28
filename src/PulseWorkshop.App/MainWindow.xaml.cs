using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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

    // Same, for the Compile - Advanced tab's own terminal drawer.
    private double _advConsoleHeightPx = 200;

    // Same, for the Package - Advanced tab's own terminal drawer.
    private double _pkgConsoleHeightPx = 200;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Title = $"PulseWorkshop v{AppVersion}";
        _vm.NavigateToDrafts += () => DraftsTab.IsSelected = true;
        _vm.NavigateToTemplates += () => TemplatesTab.IsSelected = true;
        _vm.SelectDraftRequested += id => SelectRow(DraftsList, _vm.Drafts.FirstOrDefault(d => d.Draft.Id == id));
        _vm.SelectTemplateRequested += id => SelectRow(TemplatesList, _vm.Templates.FirstOrDefault(t => t.Template.Id == id));
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.CompileAdvanced.PropertyChanged += OnAdvancedVmPropertyChanged;
        _vm.PackageAdvanced.PropertyChanged += OnPackageAdvancedVmPropertyChanged;

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
        if (_vm.IsConsoleVisible && WorkshopTab.IsSelected && ConsoleRowDef.ActualHeight > 1)
            _consoleHeightPx = ConsoleRowDef.ActualHeight;
        _settings.ConsoleVisible = _vm.IsConsoleVisible;
        _settings.ConsoleHeight = _consoleHeightPx;
        _settings.Save();
    }

    // --- Workshop terminal: Workshop-tab-only --------------------------------------------------

    /// <summary>
    /// Reacts to tab switches (the Compile inner tabs bubble their SelectionChanged up here too).
    /// The bottom terminal drawer + its toggle belong to the Workshop tab only; every other tab
    /// hides them (Game Setup has no terminal; Compile has its own embedded one).
    /// </summary>
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selector.SelectionChanged also bubbles up from combo boxes / list boxes - only react to
        // actual tab switches.
        if (e.OriginalSource is not TabControl)
            return;

        UpdateWorkshopTerminalVisibility();
    }

    /// <summary>
    /// Collapses the Workshop terminal drawer on non-Workshop tabs. (The toggle button itself lives
    /// inside the Workshop tab's action bar, so it's already Workshop-only - only the bottom drawer,
    /// which spans the whole window, needs hiding here.)
    /// </summary>
    private void UpdateWorkshopTerminalVisibility() => UpdateConsoleRowHeights();

    // --- Console drawer ------------------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsConsoleVisible))
            UpdateConsoleRowHeights();
        else if (e.PropertyName == nameof(MainViewModel.Editor))
            // Reset to the first tab when the open editor changes. The TabControl otherwise keeps its
            // SelectedIndex, so leaving it on "Danger zone" then opening a template/draft (where that
            // tab is collapsed) would leave a hidden tab selected with no matching header.
            EditorTabs.SelectedIndex = 0;
    }

    /// <summary>
    /// Expands/collapses the console rows. RowDefinition.Height can't bind cleanly to a bool, so the
    /// toggle is driven here; the dragged height is remembered across hide/show.
    /// </summary>
    private void UpdateConsoleRowHeights()
    {
        // The drawer lives at the window bottom but is a Workshop-only tool, so it stays collapsed
        // on every other tab regardless of the toggle state. (The drag handle lives in the always-on
        // terminal bar and hides itself via binding when the drawer is collapsed.)
        if (_vm.IsConsoleVisible && WorkshopTab.IsSelected)
        {
            ConsoleSplitterRowDef.Height = new GridLength(12);
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
            ConsoleSplitterRowDef.Height = new GridLength(0);
        }
    }

    // Auto-scroll to the newest line as the log grows, but only when the caret/view is already at the
    // bottom - otherwise a user scrolled up to read (or select) older output would keep getting yanked
    // down on every new line.
    private void OnConsoleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ConsoleBox.VerticalOffset >= ConsoleBox.ExtentHeight - ConsoleBox.ViewportHeight - 1)
            ConsoleBox.ScrollToEnd();
    }

    private void ScrollConsoleToEnd() => ConsoleBox.ScrollToEnd();

    // Same auto-scroll behavior for the Compile tab's embedded terminal: follow the newest studiomdl
    // line unless the user has scrolled up to read/select older output.
    private void OnCompileConsoleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (CompileConsoleBox.VerticalOffset >= CompileConsoleBox.ExtentHeight - CompileConsoleBox.ViewportHeight - 1)
            CompileConsoleBox.ScrollToEnd();
    }

    // Same auto-scroll behavior for the Compile - Advanced tab's embedded terminal.
    private void OnAdvancedConsoleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (AdvancedConsoleBox.VerticalOffset >= AdvancedConsoleBox.ExtentHeight - AdvancedConsoleBox.ViewportHeight - 1)
            AdvancedConsoleBox.ScrollToEnd();
    }

    // Same auto-scroll behavior for the Package - Advanced tab's embedded terminal.
    private void OnPackageConsoleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (PackageConsoleBox.VerticalOffset >= PackageConsoleBox.ExtentHeight - PackageConsoleBox.ViewportHeight - 1)
            PackageConsoleBox.ScrollToEnd();
    }

    // Let the user drag the Advanced "Global" command box taller/shorter.
    private void GlobalCommandResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var height = GlobalCommandBox.ActualHeight + e.VerticalChange;
        GlobalCommandBox.Height = Math.Clamp(height, 34, 400);
    }

    private void OnAdvancedVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CompileAdvancedViewModel.IsTerminalVisible))
            UpdateAdvancedTerminalRows();
    }

    private void OnPackageAdvancedVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageAdvancedViewModel.IsTerminalVisible))
            UpdatePackageTerminalRows();
    }

    /// <summary>Expands/collapses the Package terminal drawer rows (mirrors
    /// <see cref="UpdateAdvancedTerminalRows"/>).</summary>
    private void UpdatePackageTerminalRows()
    {
        if (_vm.PackageAdvanced.IsTerminalVisible)
        {
            PkgConsoleSplitterRowDef.Height = new GridLength(12);
            PkgConsoleRowDef.Height = new GridLength(_pkgConsoleHeightPx);
            PkgConsoleRowDef.MinHeight = 80;
            PackageConsoleBox.ScrollToEnd();
        }
        else
        {
            if (PkgConsoleRowDef.ActualHeight > 1)
                _pkgConsoleHeightPx = PkgConsoleRowDef.ActualHeight;
            PkgConsoleRowDef.MinHeight = 0;
            PkgConsoleRowDef.Height = new GridLength(0);
            PkgConsoleSplitterRowDef.Height = new GridLength(0);
        }
    }

    /// <summary>Expands/collapses the Advanced terminal drawer rows, remembering the dragged height
    /// across hide/show (mirrors <see cref="UpdateConsoleRowHeights"/>).</summary>
    private void UpdateAdvancedTerminalRows()
    {
        if (_vm.CompileAdvanced.IsTerminalVisible)
        {
            AdvConsoleSplitterRowDef.Height = new GridLength(12);
            AdvConsoleRowDef.Height = new GridLength(_advConsoleHeightPx);
            AdvConsoleRowDef.MinHeight = 80;
            AdvancedConsoleBox.ScrollToEnd();
        }
        else
        {
            if (AdvConsoleRowDef.ActualHeight > 1)
                _advConsoleHeightPx = AdvConsoleRowDef.ActualHeight;
            AdvConsoleRowDef.MinHeight = 0;
            AdvConsoleRowDef.Height = new GridLength(0);
            AdvConsoleSplitterRowDef.Height = new GridLength(0);
        }
    }

    // --- Advanced entries drag-to-reorder ---------------------------------------------------------
    // A reorder starts only when the press lands on a row's drag handle (Tag="DragHandle"), so the
    // text fields inside each row stay fully editable. The dropped row is moved within the project's
    // entries collection and the project is re-saved (order is persisted on save).

    private DragAdorner? _advDragAdorner;
    private AdornerLayer? _advDragLayer;

    private void AdvancedEntries_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { Tag: "DragHandle" } handle
            || handle.DataContext is not ModelEntryViewModel item)
            return;

        // Show a semi-transparent ghost of the row that follows the cursor while dragging.
        if (AdvancedEntriesList.ItemContainerGenerator.ContainerFromItem(item) is UIElement container)
        {
            _advDragLayer = AdornerLayer.GetAdornerLayer(AdvancedEntriesList);
            if (_advDragLayer is not null)
            {
                _advDragAdorner = new DragAdorner(AdvancedEntriesList, container);
                _advDragLayer.Add(_advDragAdorner);
            }
        }

        try
        {
            DragDrop.DoDragDrop(AdvancedEntriesList, item, DragDropEffects.Move);
        }
        finally
        {
            if (_advDragAdorner is not null)
            {
                _advDragLayer?.Remove(_advDragAdorner);
                _advDragAdorner = null;
                _advDragLayer = null;
            }
        }
    }

    private void AdvancedEntries_DragOver(object sender, DragEventArgs e)
    {
        if (_advDragAdorner is not null)
        {
            var pos = e.GetPosition(AdvancedEntriesList);
            _advDragAdorner.SetPosition(pos.X, pos.Y);
        }
        e.Effects = DragDropEffects.Move;
    }

    private void AdvancedEntries_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ModelEntryViewModel)) is not ModelEntryViewModel dragged
            || DataContext is not MainViewModel vm)
            return;

        var list = vm.CompileAdvanced.Entries;
        var oldIndex = list.IndexOf(dragged);
        if (oldIndex < 0)
            return;

        var target = FindEntryUnder(e.OriginalSource as DependencyObject);
        var newIndex = target is null ? list.Count - 1 : list.IndexOf(target);
        if (newIndex < 0 || newIndex == oldIndex)
            return;

        list.Move(oldIndex, newIndex);
        vm.CompileAdvanced.Save();
    }

    private static ModelEntryViewModel? FindEntryUnder(DependencyObject? source)
    {
        while (source is not null and not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.DataContext as ModelEntryViewModel;
    }

    // --- Package entries drag-to-reorder (mirrors the Advanced compile reorder) -------------------

    private DragAdorner? _pkgDragAdorner;
    private AdornerLayer? _pkgDragLayer;

    private void PackageEntries_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { Tag: "DragHandle" } handle
            || handle.DataContext is not PackageEntryViewModel item)
            return;

        if (PackageEntriesList.ItemContainerGenerator.ContainerFromItem(item) is UIElement container)
        {
            _pkgDragLayer = AdornerLayer.GetAdornerLayer(PackageEntriesList);
            if (_pkgDragLayer is not null)
            {
                _pkgDragAdorner = new DragAdorner(PackageEntriesList, container);
                _pkgDragLayer.Add(_pkgDragAdorner);
            }
        }

        try
        {
            DragDrop.DoDragDrop(PackageEntriesList, item, DragDropEffects.Move);
        }
        finally
        {
            if (_pkgDragAdorner is not null)
            {
                _pkgDragLayer?.Remove(_pkgDragAdorner);
                _pkgDragAdorner = null;
                _pkgDragLayer = null;
            }
        }
    }

    private void PackageEntries_DragOver(object sender, DragEventArgs e)
    {
        if (_pkgDragAdorner is not null)
        {
            var pos = e.GetPosition(PackageEntriesList);
            _pkgDragAdorner.SetPosition(pos.X, pos.Y);
        }
        e.Effects = DragDropEffects.Move;
    }

    private void PackageEntries_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PackageEntryViewModel)) is not PackageEntryViewModel dragged
            || DataContext is not MainViewModel vm)
            return;

        var list = vm.PackageAdvanced.Entries;
        var oldIndex = list.IndexOf(dragged);
        if (oldIndex < 0)
            return;

        var target = FindPackageEntryUnder(e.OriginalSource as DependencyObject);
        var newIndex = target is null ? list.Count - 1 : list.IndexOf(target);
        if (newIndex < 0 || newIndex == oldIndex)
            return;

        list.Move(oldIndex, newIndex);
        vm.PackageAdvanced.Save();
    }

    private static PackageEntryViewModel? FindPackageEntryUnder(DependencyObject? source)
    {
        while (source is not null and not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.DataContext as PackageEntryViewModel;
    }

    /// <summary>A translucent ghost of the dragged row, drawn in the adorner layer and moved to
    /// follow the cursor - so it's obvious a reorder is in progress.</summary>
    private sealed class DragAdorner : Adorner
    {
        private readonly System.Windows.Shapes.Rectangle _ghost;
        private double _left, _top;

        public DragAdorner(UIElement adorned, UIElement dragged) : base(adorned)
        {
            _ghost = new System.Windows.Shapes.Rectangle
            {
                Width = dragged.RenderSize.Width,
                Height = dragged.RenderSize.Height,
                Fill = new VisualBrush(dragged) { Opacity = 0.65 },
                IsHitTestVisible = false,
            };
            IsHitTestVisible = false;
        }

        public void SetPosition(double left, double top)
        {
            _left = left + 8;
            _top = top + 8;
            _advLayerUpdate();
        }

        private void _advLayerUpdate() => (Parent as AdornerLayer)?.Update(AdornedElement);

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _ghost;
        protected override Size MeasureOverride(Size constraint)
        {
            _ghost.Measure(constraint);
            return _ghost.DesiredSize;
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            _ghost.Arrange(new Rect(_ghost.DesiredSize));
            return finalSize;
        }
        public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
        {
            var group = new GeneralTransformGroup();
            if (base.GetDesiredTransform(transform) is { } baseTransform)
                group.Children.Add(baseTransform);
            group.Children.Add(new TranslateTransform(_left, _top));
            return group;
        }
    }

    /// <summary>
    /// Toggle the terminal drawer with the <c>~</c> / backtick key (like the Source engine console).
    /// Ignored while a text field has focus so the character can still be typed into titles,
    /// descriptions, paths, etc.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.OemTilde || Keyboard.FocusedElement is TextBox or PasswordBox)
            return;

        // ~ toggles whichever terminal the current tab owns: the Workshop drawer, or the
        // Compile - Advanced drawer when that tab is showing.
        if (WorkshopTab.IsSelected)
        {
            _vm.IsConsoleVisible = !_vm.IsConsoleVisible;
            e.Handled = true;
        }
        else if (CompileTab.IsSelected && CompileAdvancedTab.IsSelected)
        {
            _vm.CompileAdvanced.IsTerminalVisible = !_vm.CompileAdvanced.IsTerminalVisible;
            e.Handled = true;
        }
        else if (PackageTab.IsSelected && PackageAdvancedTab.IsSelected)
        {
            _vm.PackageAdvanced.IsTerminalVisible = !_vm.PackageAdvanced.IsTerminalVisible;
            e.Handled = true;
        }
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
    /// <summary>
    /// Selects a row in one of the lists (which opens it via the list's SelectionChanged handler) and
    /// scrolls it into view. Deferred to Background priority so a tab that was just switched to has
    /// realized its ListBox before we select/scroll.
    /// </summary>
    private void SelectRow(ListBox list, object? item)
    {
        if (item is null)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            list.SelectedItem = item;
            list.ScrollIntoView(item);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

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

    /// <summary>
    /// Danger zone: permanently delete the published item open in the editor. Confirms first
    /// (defaulting to "No") because the deletion is irreversible.
    /// </summary>
    private async void DeletePublished_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Editor is not { IsTemplateMode: false, PublishedFileId: { } id })
            return;

        var title = string.IsNullOrWhiteSpace(_vm.Editor.Title) ? id.ToString() : _vm.Editor.Title;
        var confirmed = MessageBox.Show(this,
            $"Permanently delete \"{title}\" (ID {id}) from the Steam Workshop?\n\n" +
            "This CANNOT be undone. Subscribers will lose access and the Workshop ID is gone for good.",
            "Delete published item", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

        if (confirmed)
            await _vm.DeleteCurrentPublishedAsync();
    }

    private void SaveAsTemplate_Click(object sender, RoutedEventArgs e) => _vm.SaveAsTemplate();

    /// <summary>Game Setup: clone the game whose list row's icon was clicked.</summary>
    private void GameClone_Click(object sender, RoutedEventArgs e)
    {
        if (RowData<GameSetupEntryViewModel>(sender) is { } game)
            _vm.GameSetup.CloneGame(game);
    }

    /// <summary>Game Setup: delete the game whose list row's trash icon was clicked, confirming first.</summary>
    private void GameDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RowData<GameSetupEntryViewModel>(sender) is not { } game)
            return;

        if (Confirm("Delete game", $"Delete game setup \"{game.Name}\"?"))
            _vm.GameSetup.DeleteGame(game);
    }

    /// <summary>Opens the developer's GitHub (About tab) in the default browser.</summary>
    private void OpenDeveloperGitHub_Click(object sender, RoutedEventArgs e) =>
        TryOpen(_vm.DeveloperGitHubUrl);

    /// <summary>Opens the KitsuneResource predecessor project on GitHub.</summary>
    private void OpenKitsuneResource_Click(object sender, RoutedEventArgs e) =>
        TryOpen(_vm.KitsuneResourceUrl);

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
