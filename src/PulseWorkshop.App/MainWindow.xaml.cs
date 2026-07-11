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

    // Persisted UI preferences (console window open state + bounds), loaded on startup, saved on close.
    // Owned by the view model so tabs that mutate a shared setting and this window's own bookkeeping
    // read/write the one instance instead of clobbering each other.
    private UiSettings _settings => _vm.Settings;

    // The detached, shared output console. Created lazily on first show and kept for the app's lifetime
    // (so hiding it with the X or ~ preserves its history and scroll position).
    private ConsoleWindow? _consoleWindow;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Title = $"PulseWorkshop v{AppVersion}";
#if DEBUG
        // Debug builds append a short SHA-256 of the running executable so different local builds are
        // distinguishable. Release builds omit this entirely, so official releases show a clean title.
        if (!string.IsNullOrWhiteSpace(ExecutableSha))
            Title += $" [{ExecutableSha}]";
#endif
        _vm.NavigateToDrafts += () => DraftsTab.IsSelected = true;
        _vm.NavigateToTemplates += () => TemplatesTab.IsSelected = true;
        _vm.NavigateToModelView += () => ModelViewTab.IsSelected = true;
        _vm.NavigateToUnpack += () => UnpackTab.IsSelected = true;
        _vm.SelectDraftRequested += id => SelectRow(DraftsList, _vm.Drafts.FirstOrDefault(d => d.Draft.Id == id));
        _vm.SelectTemplateRequested += id => SelectRow(TemplatesList, _vm.Templates.FirstOrDefault(t => t.Template.Id == id));
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Console.PropertyChanged += OnConsolePropertyChanged;

        // "Go to file" selects a row after navigating - bring it into view.
        _vm.Unpack.ScrollFileIntoView += row =>
            Dispatcher.BeginInvoke(() => UnpackFiles.ScrollIntoView(row),
                System.Windows.Threading.DispatcherPriority.Background);

        // Reopen the console window if it was open last time - deferred to Loaded so the main window
        // is up first.
        Loaded += (_, _) =>
        {
            if (_settings.ConsoleVisible)
                _vm.Console.IsVisible = true;

            // Restore the last-active top-level tab (out-of-range indices are ignored by WPF).
            if (_settings.MainTabIndex >= 0 && _settings.MainTabIndex < MainTabs.Items.Count)
                MainTabs.SelectedIndex = _settings.MainTabIndex;

            // Restore the last-active Simple/Advanced sub-tab of the Compile and Package tabs.
            if (_settings.CompileSubTabIndex >= 0 && _settings.CompileSubTabIndex < CompileTabs.Items.Count)
                CompileTabs.SelectedIndex = _settings.CompileSubTabIndex;
            if (_settings.PackageSubTabIndex >= 0 && _settings.PackageSubTabIndex < PackageTabs.Items.Count)
                PackageTabs.SelectedIndex = _settings.PackageSubTabIndex;
        };

        // Files referenced by package assets can be created or deleted while the user is in another
        // program; re-check them whenever the window regains focus so thumbnails and validation
        // catch up without the path having to be retyped.
        Activated += (_, _) =>
        {
            _vm.Package.RefreshFileState();
            _vm.PackageAdvanced.RefreshFileState();
            _vm.Textures.RefreshFileState();
            _vm.ModelView.RefreshFileState();
        };

        Closed += async (_, _) =>
        {
            SaveUiSettings();
            if (_consoleWindow is not null)
            {
                _consoleWindow.AllowClose();
                _consoleWindow.Close();
            }
            await _vm.DisposeAsync();
        };
    }

    /// <summary>
    /// Handles a shell file-association / "Open with" open - either this launch (a path passed on the
    /// command line) or one forwarded from a second instance. Brings the window to the front and opens
    /// the file in the tab that owns its type: <c>.pw_textureproject</c> -> Textures,
    /// <c>.pw_mdlproject</c> -> Compile - Advanced, <c>.vpk</c>/<c>.gma</c>/<c>gameinfo.txt</c> ->
    /// Unpack. An empty message (<see cref="SingleInstanceSignal.ActivateOnly"/>) just activates the
    /// window.
    /// </summary>
    public void HandleShellOpen(string? openPath)
    {
        BringToFront();

        if (string.IsNullOrWhiteSpace(openPath))
            return;

        // Packed archives (and a game's gameinfo.txt) open in the Unpack tab.
        if (PulseWorkshop.Core.Unpack.PackedArchiveLoader.CanOpen(openPath))
        {
            UnpackTab.IsSelected = true;
            _ = _vm.Unpack.OpenFromPathAsync(openPath);
            return;
        }

        if (openPath.EndsWith(".pw_textureproject", StringComparison.OrdinalIgnoreCase))
        {
            if (_vm.Textures.OpenProjectFromPath(openPath))
                TexturesTab.IsSelected = true;
            else
                ShellOpenFailed(openPath);
            return;
        }

        if (_vm.AdvancedProject.OpenProjectFromPath(openPath))
        {
            // Select the outer Compile tab and its Advanced sub-tab (the project workflow lives there).
            CompileTab.IsSelected = true;
            CompileAdvancedTab.IsSelected = true;
        }
        else
        {
            ShellOpenFailed(openPath);
        }
    }

    // --- Window-level file drop -------------------------------------------------------------------
    // Dropping a project file or an unpackable archive anywhere on the window (that isn't over a more
    // specific drop zone - the editor's content/preview zones handle and mark their own drops) routes
    // it to the tab that owns its type, exactly as a shell "Open with" launch does.

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetRoutableDroppedFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (GetRoutableDroppedFile(e) is { } path)
        {
            e.Handled = true;
            HandleShellOpen(path);
        }
    }

    /// <summary>The first dropped file the window can route to a tab (a <c>.pw_mdlproject</c> /
    /// <c>.pw_textureproject</c> project, or a <c>.vpk</c>/<c>.gma</c>/<c>gameinfo.txt</c> archive),
    /// or null if the drop carries no such file. While the Workshop tab is active, archives are not
    /// routed to Unpack - a dropped .vpk/.gma there is content for the editor's own drop zones, not
    /// something to auto-unpack.</summary>
    private string? GetRoutableDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return null;

        bool onWorkshopTab = WorkshopTab.IsSelected;

        foreach (var file in files)
        {
            if (!File.Exists(file))
                continue;
            if (file.EndsWith(".pw_mdlproject", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".pw_textureproject", StringComparison.OrdinalIgnoreCase)
                || (!onWorkshopTab && PulseWorkshop.Core.Unpack.PackedArchiveLoader.CanOpen(file)))
                return file;
        }
        return null;
    }

    private void ShellOpenFailed(string projectPath) =>
        MessageBox.Show(this,
            $"Couldn't open the project file:\n\n{projectPath}\n\nIt may be missing or not a valid PulseWorkshop project.",
            "Open project", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Restores (if minimized) and brings the window to the foreground.</summary>
    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        // Briefly toggling Topmost forces the window above others without leaving it pinned.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void SaveUiSettings()
    {
        _settings.ConsoleVisible = _vm.Console.IsVisible;
        _settings.MainTabIndex = MainTabs.SelectedIndex;
        _settings.CompileSubTabIndex = CompileTabs.SelectedIndex;
        _settings.PackageSubTabIndex = PackageTabs.SelectedIndex;
        // Remember the open Unpack archive so it can be reopened next session (lazily, on tab entry).
        _settings.UnpackLastArchive = _vm.Unpack.IsArchiveOpen ? _vm.Unpack.ArchivePath : null;
        if (_consoleWindow is not null)
        {
            // RestoreBounds gives the normal (non-maximized/minimized) placement.
            var bounds = _consoleWindow.WindowState == WindowState.Normal
                ? new Rect(_consoleWindow.Left, _consoleWindow.Top, _consoleWindow.Width, _consoleWindow.Height)
                : _consoleWindow.RestoreBounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                _settings.ConsoleWindowLeft = bounds.Left;
                _settings.ConsoleWindowTop = bounds.Top;
                _settings.ConsoleWindowWidth = bounds.Width;
                _settings.ConsoleWindowHeight = bounds.Height;
            }
        }
        _settings.Save();
    }

    // --- Detached console window ---------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Editor))
            // Preserve the user's current editor tab across entry changes. The only tab that can
            // disappear is "Danger zone" (index 2, shown only for published items); if it's selected
            // and the new editor isn't a published item, fall back to the first tab so we don't leave
            // a collapsed tab selected with no matching header.
            if (EditorTabs.SelectedIndex == 2 && _vm.Editor?.IsEditingPublished != true)
                EditorTabs.SelectedIndex = 0;
    }

    private void OnConsolePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConsoleViewModel.IsVisible))
            ApplyConsoleVisibility();
    }

    // Shows or hides the detached console window to match the shared console's IsVisible flag (toggled
    // by the ~ key or the window's own X).
    private void ApplyConsoleVisibility()
    {
        if (_vm.Console.IsVisible)
        {
            if (_consoleWindow is null)
            {
                // Deliberately not owned: an owned window minimizes/restores with its owner, and the
                // console should live independently of the main window (it's closed explicitly at shutdown).
                _consoleWindow = new ConsoleWindow(_vm.Console);
                ApplySavedConsoleBounds(_consoleWindow);
            }
            _consoleWindow.Show();
            _consoleWindow.Activate();
        }
        else
        {
            _consoleWindow?.Hide();
        }
    }

    private void ApplySavedConsoleBounds(ConsoleWindow window)
    {
        if (_settings.ConsoleWindowWidth > 0)
            window.Width = _settings.ConsoleWindowWidth;
        if (_settings.ConsoleWindowHeight > 0)
            window.Height = _settings.ConsoleWindowHeight;

        // Only honour a saved position that still lands on a visible screen; otherwise centre on screen.
        if (_settings.ConsoleWindowLeft is { } left && _settings.ConsoleWindowTop is { } top
            && IsOnScreen(left, top, window.Width, window.Height))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
        }
    }

    // True when the given rectangle overlaps the WPF virtual screen (guards against a saved position on
    // a monitor that's since been disconnected).
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return virtualScreen.IntersectsWith(new Rect(left, top, width, height));
    }

    // Let the user drag the Advanced "Global" command box taller/shorter.
    private void GlobalCommandResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var height = GlobalCommandBox.ActualHeight + e.VerticalChange;
        GlobalCommandBox.Height = Math.Clamp(height, 34, 400);
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

    // --- Texture groups drag-to-reorder (mirrors the Package entries reorder) ----------------------

    private DragAdorner? _texDragAdorner;
    private AdornerLayer? _texDragLayer;

    private void TextureGroups_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { Tag: "DragHandle" } handle
            || handle.DataContext is not TextureGroupViewModel item)
            return;

        if (TextureGroupsList.ItemContainerGenerator.ContainerFromItem(item) is UIElement container)
        {
            _texDragLayer = AdornerLayer.GetAdornerLayer(TextureGroupsList);
            if (_texDragLayer is not null)
            {
                _texDragAdorner = new DragAdorner(TextureGroupsList, container);
                _texDragLayer.Add(_texDragAdorner);
            }
        }

        try
        {
            DragDrop.DoDragDrop(TextureGroupsList, item, DragDropEffects.Move);
        }
        finally
        {
            if (_texDragAdorner is not null)
            {
                _texDragLayer?.Remove(_texDragAdorner);
                _texDragAdorner = null;
                _texDragLayer = null;
            }
        }
    }

    private void TextureGroups_DragOver(object sender, DragEventArgs e)
    {
        if (_texDragAdorner is not null)
        {
            var pos = e.GetPosition(TextureGroupsList);
            _texDragAdorner.SetPosition(pos.X, pos.Y);
        }
        e.Effects = DragDropEffects.Move;
    }

    private void TextureGroups_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TextureGroupViewModel)) is not TextureGroupViewModel dragged
            || DataContext is not MainViewModel vm)
            return;

        var list = vm.Textures.Groups;
        var oldIndex = list.IndexOf(dragged);
        if (oldIndex < 0)
            return;

        var target = FindTextureGroupUnder(e.OriginalSource as DependencyObject);
        var newIndex = target is null ? list.Count - 1 : list.IndexOf(target);
        if (newIndex < 0 || newIndex == oldIndex)
            return;

        list.Move(oldIndex, newIndex);
        vm.Textures.Save();
    }

    private static TextureGroupViewModel? FindTextureGroupUnder(DependencyObject? source)
    {
        while (source is not null and not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.DataContext as TextureGroupViewModel;
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
    /// Opens the preview for a package asset's thumbnail: the full-size image when the thumbnail
    /// shows real pixels, or the raw text in a <see cref="TextPreviewWindow"/> for a text asset.
    /// Only acts when the input file still exists on disk.
    /// </summary>
    private void AssetThumbnail_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PackageAssetViewModel asset
            || asset.ResolvedInputPath() is not { } path)
            return;

        if (asset.HasImagePreview)
            ImagePreviewWindow.ShowPreview(this, path);
        else if (asset.HasTextPreview)
            TextPreviewWindow.ShowPreview(this, path);
    }

    // --- Unpack tab -------------------------------------------------------------------------------

    // Guards the one-shot lazy reopen of the last-session Unpack archive: we restore it the first
    // time the tab is entered, never again (so a manual Close stays closed).
    private bool _unpackRestored;

    /// <summary>
    /// Fires when the outer tab strip changes. The only thing it drives is the lazy Unpack restore:
    /// reopening the last-session archive is deferred until the user actually enters the Unpack tab,
    /// so restoring a heavy gameinfo mount never slows the app's launch.
    /// </summary>
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbles from the inner tab controls (Compile/Package/editor) too - only
        // react to the outer strip's own selection.
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
            return;
        TryRestoreUnpackArchive();
    }

    private void TryRestoreUnpackArchive()
    {
        // UnpackTab can still be null if a selection event fires mid-InitializeComponent.
        if (_unpackRestored || UnpackTab is null || !UnpackTab.IsSelected)
            return;
        _unpackRestored = true; // one-shot, even if the reopen below is skipped or fails

        var path = _settings.UnpackLastArchive;
        // Nothing saved, something already open, or the file is gone since last session: skip
        // silently rather than surface an error just for entering the tab.
        if (string.IsNullOrEmpty(path) || _vm.Unpack.IsArchiveOpen || !File.Exists(path))
            return;
        _ = _vm.Unpack.OpenFromPathAsync(path);
    }

    /// <summary>Mirrors the Unpack tree's read-only SelectedItem into the view model. Only a real
    /// folder becomes the selection; a transition to null comes from the view model dropping the
    /// tree's visual selection when the file list takes over as the active pane, so it is ignored
    /// (the current folder stays the navigation context and the list stays populated).</summary>
    private void UnpackTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is UnpackFolderViewModel folder)
            _vm.Unpack.SelectedFolder = folder;
    }

    /// <summary>
    /// Double-click in the file list: a folder row navigates into that folder (Explorer-style); a
    /// file row extracts to a temp file and opens it with the user's default application for that
    /// type (so a .vtf lands in their VTF viewer instead of an internal raw-binary dump). Windows
    /// shows its "Open with" picker when no handler is registered.
    /// </summary>
    private async void UnpackFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // MouseDoubleClick fires for any button - only the left one opens/navigates, so a
        // double right-click (which also raises this) doesn't get treated as a left action.
        if (e.ChangedButton != MouseButton.Left)
            return;

        // Only act when the double-click landed on a real row (not the empty list area).
        if (e.OriginalSource is not DependencyObject origin
            || ItemsControl.ContainerFromElement(UnpackFiles, origin)
               is not ListBoxItem { DataContext: UnpackFileViewModel row })
            return;

        if (row.Folder is { } folder)
        {
            _vm.Unpack.NavigateToFolder(folder);
            return;
        }
        if (row.Entry is not { } entry)
            return;

        var path = await _vm.Unpack.ExtractForPreviewAsync(entry);
        if (path is null)
            return;

        try
        {
            // UseShellExecute routes through the shell so the file opens in whatever app the user
            // has associated (or the "Open with" dialog when there's no handler).
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _vm.Unpack.ReportPreviewOpenFailed(entry, ex);
        }
    }

    /// <summary>Enter in the Unpack search box: skip the debounce and search right away.</summary>
    private void UnpackFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        // Push the in-progress text through the binding first (it updates on PropertyChanged, but
        // be explicit so Enter always searches what is on screen).
        (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        _vm.Unpack.ApplyFilterNow();
        e.Handled = true;
    }

    /// <summary>Column-header click: sort the file list by the header's column (Tag names it).</summary>
    private void UnpackSort_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag
            && Enum.TryParse<UnpackSortColumn>(tag, out var column))
            _vm.Unpack.SortBy(column);
    }

    /// <summary>Right-click in the file list selects the row under the cursor (unless it is already
    /// part of the current multi-selection), so the context menu acts on what was clicked.</summary>
    private void UnpackFiles_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject origin
            || ItemsControl.ContainerFromElement(UnpackFiles, origin) is not ListBoxItem item)
            return;
        if (!item.IsSelected)
        {
            UnpackFiles.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    /// <summary>Context menu "Export selected...": export the highlighted rows (same as the button).</summary>
    private void UnpackExportMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Unpack.ExportSelectedCommand.CanExecute(null))
            _vm.Unpack.ExportSelectedCommand.Execute(null);
    }

    /// <summary>Tree context menu "Export folder...": export the right-clicked folder recursively.</summary>
    private void UnpackExportFolderMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is UnpackFolderViewModel folder)
            _ = _vm.Unpack.ExportFolderAsync(folder);
    }

    /// <summary>Context menu "Go to file": navigate to the folder that contains the clicked row.</summary>
    private void UnpackGoToMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is UnpackFileViewModel row)
            _vm.Unpack.GoToFile(row);
    }

    /// <summary>
    /// Toggle the shared console window with the <c>~</c> / backtick key (like the Source engine
    /// console), from any tab. Ignored while a text field has focus so the character can still be typed
    /// into titles, descriptions, paths, etc.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // Ctrl+Shift+S: "Save project as" for the project owned by the active tab - the shared
        // .pw_mdlproject on the Compile/Package tabs, the .pw_textureproject on the Textures tab.
        // Works even with a text field focused (it's a command, not a typeable character).
        if (e.Key == Key.S
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (TexturesTab.IsSelected)
            {
                _vm.Textures.SaveProjectAs();
                e.Handled = true;
            }
            else if (CompileTab.IsSelected || PackageTab.IsSelected)
            {
                _vm.AdvancedProject.SaveProjectAs();
                e.Handled = true;
            }
            return;
        }

        // ~ / backtick toggles the shared console, unless a text field has focus (so it stays typeable).
        if (e.Key != Key.OemTilde || Keyboard.FocusedElement is TextBox or PasswordBox)
            return;

        _vm.Console.IsVisible = !_vm.Console.IsVisible;
        e.Handled = true;
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

#if DEBUG
    /// <summary>Short SHA-256 (first 8 hex chars) of the compiled app assembly, shown in the window
    /// title on Debug builds so distinct local builds are distinguishable. Hashes the assembly file
    /// (PulseWorkshop.dll) rather than the thin apphost .exe, which stays constant across rebuilds.
    /// Null if the file can't be read.</summary>
    private static string? ExecutableSha
    {
        get
        {
            try
            {
                var path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;
                using var stream = File.OpenRead(path);
                var hash = System.Security.Cryptography.SHA256.HashData(stream);
                return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }
    }
#endif

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

    private void DraftClearSelection_Click(object sender, RoutedEventArgs e) => _vm.SetAllDraftsSelected(false);

    /// <summary>
    /// Bulk publish/save: confirms first (spelling out how many are edits vs new items), then
    /// publishes every ticked draft. New-item drafts missing requirements are skipped and reported.
    /// </summary>
    private async void PublishSelectedDrafts_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasDraftSelection || _vm.IsBusy)
            return;

        var summary = _vm.DescribeSelectedDraftPublish();
        var message =
            $"This will {summary} for the {_vm.SelectedDraftCount} selected draft(s).\n\n" +
            "New-item drafts still missing a title, description, content file, or preview image are " +
            "skipped. Continue?";

        if (Confirm("Publish selected drafts", message))
            await _vm.PublishSelectedDraftsAsync();
    }

    private void DeleteSelectedDrafts_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasDraftSelection || _vm.IsBusy)
            return;

        var message =
            $"Revert the {_vm.SelectedDraftCount} selected draft(s)?\n\n" +
            "For drafts that track edits to a published item, this discards the unsaved edits and " +
            "reverts to the published item. Unpublished drafts are removed. The Workshop items " +
            "themselves are NOT deleted.";

        if (Confirm("Revert selected drafts", message))
            _vm.DeleteSelectedDrafts();
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

        // Directory mode (editing a published item with a known filename): pick a folder and let the
        // editor auto-resolve the published file from it.
        if (Editor.UsesDirectoryContentInput)
        {
            var folder = new OpenFolderDialog
            {
                Title = $"Choose the folder containing {Editor.PublishedContentFileName}",
            };
            if (folder.ShowDialog(this) == true)
                Editor.ContentDirectory = folder.FolderName;
            return;
        }

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
        // Directory mode accepts a folder (auto-pick) or a content file (swap entirely).
        bool ok = Editor is { UsesDirectoryContentInput: true }
            ? GetDroppedDirectory(e) is not null || IsSingleFileDrop(e, Editor.ContentFileExtension)
            : IsSingleFileDrop(e, Editor?.ContentFileExtension);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ContentDrop_Drop(object sender, DragEventArgs e)
    {
        // The drop belongs to the content zone - mark it handled so it doesn't bubble up to
        // Window_Drop, which would route the same .vpk/.gma to the Unpack tab ("auto-unpack").
        e.Handled = true;

        if (Editor is null)
            return;

        if (Editor.UsesDirectoryContentInput)
        {
            // A dropped folder or a dropped content file both flow through ContentDirectory, which
            // resolves a file path as-is and a folder by auto-picking the published filename.
            var path = GetDroppedDirectory(e) ?? GetDroppedFile(e, Editor.ContentFileExtension);
            if (path is not null)
                Editor.ContentDirectory = path;
            return;
        }

        var file = GetDroppedFile(e, Editor.ContentFileExtension);
        if (file is not null)
            Editor.ContentFile = file;
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
        e.Handled = true;
        if (Editor is null)
            return;
        var path = GetDroppedFile(e, ImageExts);
        if (path is not null)
            Editor.PreviewImagePath = path;
    }

    // --- Drag-drop helpers ---------------------------------------------------------------------

    private static bool IsSingleFileDrop(DragEventArgs e, params string?[] allowedExts) =>
        GetDroppedFile(e, allowedExts) is not null;

    /// <summary>The path of a single dropped folder, or null if the drop isn't exactly one directory.</summary>
    private static string? GetDroppedDirectory(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1)
            return null;
        return Directory.Exists(paths[0]) ? paths[0] : null;
    }

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
