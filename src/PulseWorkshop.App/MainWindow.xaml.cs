using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PulseWorkshop.App.Rendering;
using PulseWorkshop.App.Services;
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
        // Before InitializeComponent: the PaneSize attached property restores each tagged splitter
        // pane while the XAML is parsed, so it needs the settings first.
        PaneSize.Attach(_settings);
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
        // One picker for the whole window, so this subscribes once rather than per previewed model.
        _vm.Unpack.SkinChanged += OnSkinChanged;
        _vm.Unpack.ShowSkeletonChanged += OnShowSkeletonChanged;
        _vm.SelectDraftRequested += id => SelectRow(DraftsList, _vm.Drafts.FirstOrDefault(d => d.Draft.Id == id));
        _vm.SelectTemplateRequested += id => SelectRow(TemplatesList, _vm.Templates.FirstOrDefault(t => t.Template.Id == id));
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Console.PropertyChanged += OnConsolePropertyChanged;

        // "Go to file" selects a row after navigating - bring it into view.
        _vm.Unpack.ScrollFileIntoView += row =>
            Dispatcher.BeginInvoke(() => UnpackFiles.ScrollIntoView(row),
                System.Windows.Threading.DispatcherPriority.Background);

        // Details pane: reload the thumbnail when its subject changes, and apply the open/width
        // state (and refresh the thumbnail) when the pane is toggled or the alpha option flips.
        _vm.Unpack.DetailChanged += () => _ = UpdateDetailThumbnailAsync();
        _vm.Unpack.PropertyChanged += OnUnpackPropertyChanged;
        ApplyDetailsPaneState();

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
            StopModelLoop();
            _modelPreview?.Dispose();
            _modelPreview = null;
            if (_consoleWindow is not null)
            {
                _consoleWindow.AllowClose();
                _consoleWindow.Close();
            }
            await _vm.DisposeAsync();
        };
    }

    /// <summary>Handles a shell "Open with" open - this launch's command line, or one forwarded from a
    /// second instance - by activating the window and routing the file to the tab that owns its type:
    /// project files to Textures / Compile - Advanced, archives and gameinfo.txt to Unpack.</summary>
    /// <remarks>An empty message (<see cref="SingleInstanceSignal.ActivateOnly"/>) only activates.</remarks>
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
    // A project file or archive dropped anywhere on the window routes to the tab that owns its type,
    // exactly as a shell "Open with" launch does. Drop zones that handle their own drops (the
    // editor's content/preview) mark them handled and never reach here.

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

    /// <summary>The first dropped file the window can route to a tab - a project file or an
    /// archive/gameinfo.txt - or null if the drop carries none.</summary>
    /// <remarks>Archives are not routed while the Workshop tab is active: a .vpk dropped there is
    /// content for the editor's own drop zones, not something to auto-unpack.</remarks>
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
        // ... and where in it the user was, so reopening lands on the same folder and file.
        _settings.UnpackLastFolder = _vm.Unpack.IsArchiveOpen ? _vm.Unpack.SelectedFolder?.FullPath : null;
        _settings.UnpackLastFile = _vm.Unpack.IsArchiveOpen ? _vm.Unpack.DetailEntry?.Path : null;
        PaneSize.Save(_settings);
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
            // Preserve the editor tab across entry changes. "Danger zone" (index 2) is the only one
            // that can disappear - it shows for published items only - so fall back to the first tab
            // rather than leave a collapsed tab selected with no header.
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

    // --- Drag-to-reorder lists --------------------------------------------------------------------
    // Shared by the Compile/Package advanced entry lists, the texture groups and the Game Setup games
    // list. A reorder starts only from a row's drag handle (Tag="DragHandle"), so the controls inside
    // each row stay usable; a ghost follows the cursor and the owner persists the order on drop.

    private DragAdorner? _dragAdorner;
    private AdornerLayer? _dragLayer;

    /// <summary>Starts a reorder drag of the pressed row (no-op unless the press hit a drag handle).</summary>
    private void BeginReorderDrag<T>(ListBox list, MouseButtonEventArgs e) where T : class
    {
        if (e.OriginalSource is not FrameworkElement { Tag: "DragHandle" } handle
            || handle.DataContext is not T item)
            return;

        if (list.ItemContainerGenerator.ContainerFromItem(item) is UIElement container)
        {
            _dragLayer = AdornerLayer.GetAdornerLayer(list);
            if (_dragLayer is not null)
            {
                _dragAdorner = new DragAdorner(list, container);
                _dragLayer.Add(_dragAdorner);
            }
        }

        try
        {
            DragDrop.DoDragDrop(list, item, DragDropEffects.Move);
        }
        finally
        {
            if (_dragAdorner is not null)
            {
                _dragLayer?.Remove(_dragAdorner);
                _dragAdorner = null;
                _dragLayer = null;
            }
        }
    }

    /// <summary>Moves the drag ghost. Only claims reorder drags - a genuine file drop over the list
    /// falls through to the window-level handler.</summary>
    private void ReorderDragOver<T>(ListBox list, DragEventArgs e) where T : class
    {
        if (e.Data.GetData(typeof(T)) is not T)
            return;
        if (_dragAdorner is not null)
        {
            var pos = e.GetPosition(list);
            _dragAdorner.SetPosition(pos.X, pos.Y);
        }
        e.Effects = DragDropEffects.Move;
        // Stop the bubble here so the window-level file-drop handler doesn't override the effect.
        e.Handled = true;
    }

    /// <summary>Resolves the dragged row and the row it was dropped on, then calls
    /// <paramref name="move"/> with (oldIndex, newIndex). Dropping past the last row moves to the end.</summary>
    private static void ReorderDrop<T>(IList<T> list, DragEventArgs e, Action<int, int> move) where T : class
    {
        if (e.Data.GetData(typeof(T)) is not T dragged)
            return;
        e.Handled = true;

        var oldIndex = list.IndexOf(dragged);
        if (oldIndex < 0)
            return;

        var target = FindRowItem<T>(e.OriginalSource as DependencyObject);
        var newIndex = target is null ? list.Count - 1 : list.IndexOf(target);
        if (newIndex < 0 || newIndex == oldIndex)
            return;

        move(oldIndex, newIndex);
    }

    /// <summary>The item behind the dropped-on visual, or null when the drop missed every row.</summary>
    private static T? FindRowItem<T>(DependencyObject? source) where T : class
    {
        while (source is not null and not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.DataContext as T;
    }

    private void AdvancedEntries_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BeginReorderDrag<ModelEntryViewModel>(AdvancedEntriesList, e);

    private void AdvancedEntries_DragOver(object sender, DragEventArgs e) =>
        ReorderDragOver<ModelEntryViewModel>(AdvancedEntriesList, e);

    private void AdvancedEntries_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var list = vm.CompileAdvanced.Entries;
        ReorderDrop(list, e, (from, to) =>
        {
            list.Move(from, to);
            vm.CompileAdvanced.Save();
        });
    }

    private void PackageEntries_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BeginReorderDrag<PackageEntryViewModel>(PackageEntriesList, e);

    private void PackageEntries_DragOver(object sender, DragEventArgs e) =>
        ReorderDragOver<PackageEntryViewModel>(PackageEntriesList, e);

    private void PackageEntries_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var list = vm.PackageAdvanced.Entries;
        ReorderDrop(list, e, (from, to) =>
        {
            list.Move(from, to);
            vm.PackageAdvanced.Save();
        });
    }

    private void TextureGroups_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BeginReorderDrag<TextureGroupViewModel>(TextureGroupsList, e);

    private void TextureGroups_DragOver(object sender, DragEventArgs e) =>
        ReorderDragOver<TextureGroupViewModel>(TextureGroupsList, e);

    private void TextureGroups_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var list = vm.Textures.Groups;
        ReorderDrop(list, e, (from, to) =>
        {
            list.Move(from, to);
            vm.Textures.Save();
            // Run order decides which group claims a file, so the match preview changes with the order.
            vm.Textures.RequestMatchRefresh();
        });
    }

    private void GameSetupGames_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BeginReorderDrag<GameSetupEntryViewModel>(GameSetupGamesList, e);

    private void GameSetupGames_DragOver(object sender, DragEventArgs e) =>
        ReorderDragOver<GameSetupEntryViewModel>(GameSetupGamesList, e);

    private void GameSetupGames_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        // MoveGame reorders the persisted config alongside the list, then saves.
        ReorderDrop(vm.GameSetup.Games, e, vm.GameSetup.MoveGame);
    }

    // --- Entry/group list context menus (Duplicate/Delete selected) --------------------------------
    // Right-clicking a row outside the current highlight selects just it first (Explorer behaviour),
    // so the menu's batch action always includes the row pointed at; a row already in a multi-selection
    // keeps it. The items then run the list VM's batch operation over every highlighted row.

    /// <summary>If the right-clicked row isn't already selected, select only it (keeps an existing
    /// multi-selection intact when the row is part of it).</summary>
    private static void SelectRowOnRightClick(ListBox list, object originalSource)
    {
        if (originalSource is not DependencyObject origin
            || ItemsControl.ContainerFromElement(list, origin) is not ListBoxItem item)
            return;
        if (!item.IsSelected)
        {
            list.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void AdvancedEntries_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => SelectRowOnRightClick(AdvancedEntriesList, e.OriginalSource);

    private void AdvancedEntriesDuplicateSelected_Click(object sender, RoutedEventArgs e)
        => _vm.CompileAdvanced.CloneSelectedEntries();

    private void AdvancedEntriesDeleteSelected_Click(object sender, RoutedEventArgs e)
        => _vm.CompileAdvanced.RemoveSelectedEntries();

    private void PackageEntries_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => SelectRowOnRightClick(PackageEntriesList, e.OriginalSource);

    private void PackageEntriesDuplicateSelected_Click(object sender, RoutedEventArgs e)
        => _vm.PackageAdvanced.CloneSelectedEntries();

    private void PackageEntriesDeleteSelected_Click(object sender, RoutedEventArgs e)
        => _vm.PackageAdvanced.RemoveSelectedEntries();

    private void TextureGroups_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => SelectRowOnRightClick(TextureGroupsList, e.OriginalSource);

    private void TextureGroupsDuplicateSelected_Click(object sender, RoutedEventArgs e)
        => _vm.Textures.CloneSelectedGroups();

    private void TextureGroupsDeleteSelected_Click(object sender, RoutedEventArgs e)
        => _vm.Textures.RemoveSelectedGroups();

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

    /// <summary>Opens a package asset's thumbnail: the full-size image when the thumbnail shows real
    /// pixels, or the raw text in a <see cref="TextPreviewWindow"/>. Only acts when the input file
    /// still exists on disk.</summary>
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

    /// <summary>Opens the full-size image for a clicked tile in the Textures match preview. Files
    /// nothing could decode (the tile shows a shell icon) have nothing to show, so they no-op.</summary>
    private void TextureMatch_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TextureMatchViewModel match && match.HasImagePreview)
            ImagePreviewWindow.ShowPreview(this, match.FullPath);
    }

    // --- Unpack tab -------------------------------------------------------------------------------

    // Guards the one-shot lazy reopen of the last-session Unpack archive: we restore it the first
    // time the tab is entered, never again (so a manual Close stays closed).
    private bool _unpackRestored;

    /// <summary>Fires when the outer tab strip changes. Its only job is the lazy Unpack restore:
    /// reopening last session's archive waits until the user enters the Unpack tab, so a heavy
    /// gameinfo mount never slows the app's launch.</summary>
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

    /// <summary>Mirrors the Unpack tree's read-only SelectedItem into the view model - only a real
    /// folder becomes the selection. Null is ignored: it means the VM dropped the tree's visual
    /// selection to hand the file list the active pane, and that folder stays the context.</summary>
    private void UnpackTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is UnpackFolderViewModel folder)
            _vm.Unpack.SelectedFolder = folder;
    }

    /// <summary>Double-click in the file list: a folder row navigates into it (Explorer-style), a file
    /// row extracts to temp and opens in the user's default app for that type - Windows shows its
    /// "Open with" picker when nothing is registered.</summary>
    /// <remarks>Holding Alt reveals the extracted file in Explorer, like every other open action.</remarks>
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
            // Opens in whatever app the user has associated (or the shell's "Open with" dialog
            // when there's no handler) - or reveals the extracted file when Alt is held.
            ShellOpen.Open(path);
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

    /// <summary>Re-syncs the row VMs' <c>IsSelected</c> from the ListBox, the authority. A row
    /// highlighted while virtualized out isn't in <c>SelectedItems</c> for the next click to clear, so
    /// it stays selected in the VM and the Details pane freezes on that multi-selection.</summary>
    private void UnpackFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = new HashSet<object>(UnpackFiles.SelectedItems.Cast<object>());
        foreach (var row in _vm.Unpack.Files)
            row.IsSelected = selected.Contains(row);
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

    /// <summary>Decodes a raster image to a frozen thumbnail bitmap, capping the decode width so a
    /// large source can't build a giant bitmap. <paramref name="srcW"/>/<paramref name="srcH"/> come
    /// from metadata, so the capped decode doesn't misreport them. Null if WPF can't decode it.</summary>
    private static BitmapSource? TryDecodeImage(byte[] bytes, out int srcW, out int srcH)
    {
        srcW = srcH = 0;
        try
        {
            // Metadata-only pass for the true dimensions (no full pixel decode).
            var probe = BitmapFrame.Create(new MemoryStream(bytes),
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            srcW = probe.PixelWidth;
            srcH = probe.PixelHeight;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (srcW > 512) // only downscale; never upscale a small source
                bmp.DecodePixelWidth = 512;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes a previewable Unpack entry to a frozen thumbnail, shared by the hover popup and
    /// the Details pane: a .vtf or .vtex_c through our lite readers, or a raster image through WPF.
    /// Returns the bitmap (null when unavailable), the source's true size, and a format label.</summary>
    private static async Task<(BitmapSource? Image, int SrcW, int SrcH, string Fmt)> DecodePreviewAsync(
        UnpackPreviewKind kind, string ext, byte[] bytes, int minSize)
    {
        switch (kind)
        {
            case UnpackPreviewKind.Vtf:
                return await Task.Run(() =>
                {
                    var vtf = PulseWorkshop.Core.Unpack.VtfImage.Decode(bytes, minSize);
                    if (vtf is null)
                        return ((BitmapSource?)null, 0, 0, "VTF");
                    var img = BitmapSource.Create(vtf.Width, vtf.Height, 96, 96,
                        PixelFormats.Bgra32, null, vtf.Bgra, vtf.Width * 4);
                    img.Freeze();
                    return ((BitmapSource?)img, vtf.SourceWidth, vtf.SourceHeight, "VTF");
                });
            case UnpackPreviewKind.Vtex:
                // Source 2 textures (BC7 etc.) can be heavier to decode - do it off the UI thread.
                return await Task.Run(() =>
                {
                    var tex = PulseWorkshop.Core.Unpack.Source2Texture.Decode(bytes, minSize);
                    if (tex is null)
                        return ((BitmapSource?)null, 0, 0, "VTEX_C");
                    if (tex.RawImage is not null)
                        return (TryDecodeImage(tex.RawImage, out _, out _), tex.SourceWidth, tex.SourceHeight, tex.FormatName);
                    var img = BitmapSource.Create(tex.Width, tex.Height, 96, 96,
                        PixelFormats.Bgra32, null, tex.Bgra!, tex.Width * 4);
                    img.Freeze();
                    return ((BitmapSource?)img, tex.SourceWidth, tex.SourceHeight, tex.FormatName);
                });
            default:
                var image = TryDecodeImage(bytes, out int w, out int h);
                return (image, w, h, ext.ToUpperInvariant());
        }
    }

    // --- Unpack Details pane ---------------------------------------------------------------------
    //
    // The Explorer-style pane on the right of the file list: a thumbnail over a few info rows for the
    // current selection. The textual fields are bound from the view model; the thumbnail is decoded
    // here (WPF), and the pane's width / collapsed state map to its grid columns.

    private const double DetailsPaneMinWidth = 180;

    /// <summary>Largest entry we'll pull into memory to decode a Details pane thumbnail.</summary>
    private const int UnpackPreviewMaxBytes = 64 * 1024 * 1024;

    /// <summary>Largest entry shown by the text preview - well below the image cap, since it's the
    /// whole file rendered in a WPF TextBox rather than a decoded bitmap.</summary>
    private const int TextPreviewMaxBytes = 2 * 1024 * 1024;

    /// <summary>Decodes a text-preview entry's bytes, honoring a UTF-8/UTF-16 BOM when present and
    /// falling back to UTF-8 (Source's .cfg/.nut/.lua/.txt files are ASCII/UTF-8 in practice).</summary>
    private static string DecodeText(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private int _detailThumbGeneration;

    /// <summary>Reacts to Unpack VM changes that affect the Details pane: the pane toggle (apply the
    /// column/splitter state, then reload) and the alpha option (re-tint the thumbnail).</summary>
    private void OnUnpackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UnpackViewModel.IsDetailsPaneOpen))
        {
            ApplyDetailsPaneState();
            _ = UpdateDetailThumbnailAsync();
        }
        else if (e.PropertyName == nameof(UnpackViewModel.PreviewAlpha))
        {
            _ = UpdateDetailThumbnailAsync();
        }
    }

    /// <summary>Maps the persisted Details pane state onto its grid columns: the saved width (and an
    /// 8px splitter) when open, or zeroed columns so the file list reclaims the space when collapsed.</summary>
    private void ApplyDetailsPaneState()
    {
        if (UnpackDetailsColumn is null)
            return; // called before the template is realized

        UnpackDetailThumb.Height = Math.Clamp(_settings.UnpackDetailsThumbHeight,
            DetailsThumbMinHeight, DetailsThumbMaxHeight);

        if (_vm.Unpack.IsDetailsPaneOpen)
        {
            var w = Math.Max(DetailsPaneMinWidth, _settings.UnpackDetailsPaneWidth);
            UnpackDetailsColumn.Width = new GridLength(w);
            UnpackDetailsSplitterColumn.Width = new GridLength(8);
        }
        else
        {
            UnpackDetailsColumn.Width = new GridLength(0);
            UnpackDetailsSplitterColumn.Width = new GridLength(0);
        }
    }

    /// <summary>Persists the Details pane's new width after the user drags its splitter (clamped so it
    /// can't be shrunk into uselessness).</summary>
    private void UnpackDetailsSplitter_DragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var w = UnpackDetailsColumn.ActualWidth;
        if (w < DetailsPaneMinWidth)
        {
            w = DetailsPaneMinWidth;
            UnpackDetailsColumn.Width = new GridLength(w);
        }
        _settings.UnpackDetailsPaneWidth = w;
        _settings.Save();
    }

    /// <summary>Loads the Details pane thumbnail for the current subject: a decoded texture/image, or
    /// a generic file/folder glyph when there is no preview. Own generation counter so a slow decode
    /// whose subject changed is discarded.</summary>
    private async Task UpdateDetailThumbnailAsync()
    {
        if (UnpackDetailImage is null)
            return; // template not realized yet

        int gen = ++_detailThumbGeneration;
        var vm = _vm.Unpack;

        // Start from the generic glyph; a successful decode (or model load) replaces it below.
        UnpackDetailImage.Source = null;
        UnpackDetailImage.Visibility = Visibility.Collapsed;
        UnpackDetailText.Text = string.Empty;
        UnpackDetailText.Visibility = Visibility.Collapsed;
        UnpackDetailModel.Source = null;
        UnpackDetailModel.Visibility = Visibility.Collapsed;
        UnpackDetailModelHint.Visibility = Visibility.Collapsed;
        UnpackDetailModelFps.Visibility = Visibility.Collapsed;
        _modelPreviewActive = false;
        _modelBitmap = null;
        StopModelLoop();
        UnpackDetailFallback.Visibility = Visibility.Visible;
        bool isFolder = vm.DetailIsFolder;
        UnpackDetailFolderGlyph.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
        UnpackDetailFileGlyph.Visibility = isFolder ? Visibility.Collapsed : Visibility.Visible;
        UnpackDetailFallbackExt.Text = vm.DetailExtensionChip;

        if (!vm.IsDetailsPaneOpen || vm.DetailEntry is not { } entry
            || vm.DetailPreviewKind == UnpackPreviewKind.None)
            return;

        // A .mdl is not an image - it goes to the Vulkan preview, which owns its own load path
        // (it needs the .vvd/.vtx siblings too) and leaves the glyph up if anything fails.
        if (vm.DetailPreviewKind == UnpackPreviewKind.Model)
        {
            await LoadModelPreviewAsync(entry, gen);
            return;
        }

        if (vm.DetailPreviewKind == UnpackPreviewKind.Text)
        {
            var textBytes = await vm.ReadEntryBytesAsync(entry, TextPreviewMaxBytes, CancellationToken.None);
            if (textBytes is null || gen != _detailThumbGeneration)
                return; // too large, unreadable, or subject moved on - keep the generic glyph
            UnpackDetailText.Text = DecodeText(textBytes);
            UnpackDetailText.Visibility = Visibility.Visible;
            UnpackDetailFallback.Visibility = Visibility.Collapsed;
            return;
        }

        var bytes = await vm.ReadEntryBytesAsync(entry, UnpackPreviewMaxBytes, CancellationToken.None);
        if (bytes is null || gen != _detailThumbGeneration)
            return;

        var (image, _, _, _) = await DecodePreviewAsync(vm.DetailPreviewKind, entry.Extension, bytes, minSize: 256);
        if (gen != _detailThumbGeneration || image is null)
            return; // decode failed or subject moved on - keep the generic glyph

        if (!vm.PreviewAlpha)
            image = TexturePreview.ForceOpaque(image);
        UnpackDetailImage.Source = image;
        UnpackDetailImage.Visibility = Visibility.Visible;
        UnpackDetailFallback.Visibility = Visibility.Collapsed;
    }

    // --- Unpack Details pane: 3D model preview ------------------------------------------------------
    //
    // Highlighting a .mdl swaps the thumbnail for a live render of its mesh over a ground grid, orbited
    // with the mouse. Rendering/VulkanModelPreview.cs draws offscreen and hands back BGRA pixels, so to
    // WPF this is still a bitmap in an Image - no HwndHost, no airspace issues inside the pane.
    //
    // Everything here runs on the UI thread on purpose: a Vulkan device needs external synchronization
    // and a render at this size is sub-millisecond. Only the .mdl/.vvd/.vtx parse is backgrounded, in
    // the view model, before any of this is touched.

    /// <summary>Opening view: a three-quarter front angle, slightly above. Yaw is measured from +X and
    /// dead-front is 0, because <c>VulkanModelPreview._modelOrientation</c> leaves every model facing
    /// +X as HLMV presents it; the 0.55 rad offset swings the eye round to the model's front-left.</summary>
    private const float ModelDefaultYaw = 0.55f, ModelDefaultPitch = 0.35f;

    /// <summary>How far the pitch may travel before the look-at basis degenerates at the poles.</summary>
    private const float ModelMaxPitch = 1.50f;

    /// <summary>Cap on the offscreen target, so dragging the pane very wide on a HiDPI screen can't
    /// ask for a silly allocation.</summary>
    private const int ModelPreviewMaxPixels = 2048;

    /// <summary>Target size is snapped down to a multiple of this. Resizing the pane otherwise tears
    /// down and rebuilds the whole offscreen image set on every pixel of the drag; the few pixels of
    /// slack sit against a background the render clears to anyway, so they don't show.</summary>
    private const int ModelPreviewSizeStep = 8;

    /// <summary>Ceiling for the live preview, deliberately far above any refresh rate: the loop rides
    /// the compositor's vsync-paced tick and is already bounded by the display. A cap below the refresh
    /// rate only reaches integer divisions of it - 120 on a 144 Hz screen drops to 72.</summary>
    private const double ModelMinFrameMs = 1000.0 / 240.0;

    /// <summary>A render slower than this makes the loop sit out the next tick, so a heavy model on a
    /// weak GPU halves its frame rate instead of making the whole window feel sluggish.</summary>
    private const double ModelFrameBudgetMs = 5.0;

    private bool _modelSkipNextTick;

    private VulkanModelPreview? _modelPreview;

    /// <summary>Set once Vulkan has failed to come up, so every later .mdl skips straight to the glyph
    /// instead of retrying (and re-logging) device creation.</summary>
    private bool _modelPreviewUnavailable;

    /// <summary>True while the pane is actually showing a model - gates the mouse handlers and resize
    /// redraws, which the texture previews must not react to.</summary>
    private bool _modelPreviewActive;

    private float _modelYaw = ModelDefaultYaw, _modelPitch = ModelDefaultPitch, _modelZoom = 1f;

    /// <summary>Where the camera is looking, sideways and up, as a fraction of the model's framing
    /// distance (see <see cref="VulkanModelPreview.Render"/>). Moved by Shift+drag, as in HLMV.</summary>
    private System.Numerics.Vector2 _modelPan;

    private Point _modelDragFrom;
    private bool _modelDragging;

    /// <summary>True when the drag that is underway pans instead of orbiting. Latched at mouse-down
    /// so letting go of Ctrl mid-drag doesn't switch to spinning the model.</summary>
    private bool _modelPanning;

    // The preview renders continuously off the compositor's frame tick rather than once per input
    // event: input handlers only move the camera, so a fast drag can't queue up a burst of blocking
    // submits, and the frame cap holds the cost steady whatever the display's refresh rate is.
    private bool _modelLoopRunning;
    private readonly System.Diagnostics.Stopwatch _modelClock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _modelLastFrameAt;
    private double _modelFps;

    /// <summary>Reused across frames - a fresh BitmapSource per frame is what made a fast orbit
    /// stutter, since each one allocated a full-size pixel array for the GC to collect.</summary>
    private WriteableBitmap? _modelBitmap;

    private void StartModelLoop()
    {
        if (_modelLoopRunning)
            return;
        _modelLoopRunning = true;
        _modelLastFrameAt = default;
        CompositionTarget.Rendering += OnModelFrame;
    }

    private void StopModelLoop()
    {
        if (!_modelLoopRunning)
            return;
        _modelLoopRunning = false;
        CompositionTarget.Rendering -= OnModelFrame;
    }

    /// <summary>One live frame, rate-capped. Skips entirely while the pane is off-screen (another tab
    /// selected, Details collapsed) so an idle model isn't spinning the GPU in the background.</summary>
    private void OnModelFrame(object? sender, EventArgs e)
    {
        if (!_modelPreviewActive || !UnpackDetailThumb.IsVisible)
            return;

        // Graceful degradation rather than a fixed cap: only a frame that actually overran gives up
        // the next tick.
        if (_modelSkipNextTick)
        {
            _modelSkipNextTick = false;
            return;
        }

        var now = _modelClock.Elapsed;
        double sinceLast = (now - _modelLastFrameAt).TotalMilliseconds;
        if (_modelLastFrameAt != default && sinceLast < ModelMinFrameMs)
            return;
        _modelLastFrameAt = now;

        // Smoothed, so the readout is legible instead of flickering through every jittered frame.
        if (sinceLast is > 0 and < 1000)
            _modelFps = _modelFps <= 0 ? 1000.0 / sinceLast : _modelFps * 0.9 + (1000.0 / sinceLast) * 0.1;

        RenderModelPreview();
        _modelSkipNextTick = _modelPreview?.LastFrameMilliseconds > ModelFrameBudgetMs;
    }

    /// <summary>Loads a .mdl into the Vulkan preview and draws the first frame, or leaves the generic
    /// glyph in place when the model has no usable mesh (or this machine has no Vulkan).</summary>
    private async Task LoadModelPreviewAsync(PulseWorkshop.Core.Unpack.PackedEntry entry, int gen)
    {
        var vm = _vm.Unpack;
        if (_modelPreviewUnavailable)
            return;

        var model = await vm.ReadModelMeshAsync(entry, CancellationToken.None);
        if (model is null || gen != _detailThumbGeneration)
            return;

        // The device is built on first use rather than at startup - most sessions never preview a
        // model, and bringing up Vulkan costs a few hundred milliseconds.
        if (_modelPreview is null)
        {
            _modelPreview = VulkanModelPreview.TryCreate(vm.LogPreview);
            if (_modelPreview is null)
            {
                _modelPreviewUnavailable = true;
                return;
            }
        }

        try
        {
            _modelPreview.SetMesh(model.Mesh, model.Materials);
        }
        catch (Exception ex)
        {
            vm.LogPreview($"3D preview: could not upload the mesh - {ex.Message}");
            return;
        }

        // Bodygroup pickers for whatever the model actually lets you choose. Each one drives the
        // renderer directly - no reupload, the draw loop just skips the sub-models not selected.
        vm.SetBodyGroups(_modelPreview.BodyParts);
        foreach (var group in vm.BodyGroups)
            group.PropertyChanged += OnBodyGroupChanged;
        vm.SetSkins(_modelPreview.SkinCount);
        _modelPreview.ShowSkeleton = vm.ShowSkeleton;
        vm.SetSkeleton(_modelPreview.HasSkeleton);

        _modelYaw = ModelDefaultYaw;
        _modelPitch = ModelDefaultPitch;
        _modelZoom = 1f;
        _modelPan = default;
        _modelPreviewActive = true;
        _modelFps = 0;
        UnpackDetailFallback.Visibility = Visibility.Collapsed;
        UnpackDetailModel.Visibility = Visibility.Visible;
        UnpackDetailModelHint.Visibility = Visibility.Visible;
        UnpackDetailModelFps.Visibility = Visibility.Visible;
        RenderModelPreview();
        StartModelLoop();
    }

    /// <summary>The skin picker changed - the renderer re-points the parts at the new skin family.</summary>
    private void OnSkinChanged(int skin) => _modelPreview?.SetSkin(skin);

    /// <summary>The skeleton toggle flipped. The frame loop picks it up on its next tick, so there is
    /// nothing to render here.</summary>
    private void OnShowSkeletonChanged(bool show)
    {
        if (_modelPreview is not null)
            _modelPreview.ShowSkeleton = show;
    }

    /// <summary>A bodygroup picker changed - show that sub-model instead. The frame loop redraws on
    /// its own tick, so there is nothing to render here.</summary>
    private void OnBodyGroupChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BodyGroupViewModel.SelectedIndex)
            || sender is not BodyGroupViewModel group)
            return;
        _modelPreview?.SetBodyGroup(group.Index, group.SelectedIndex);
    }

    // --- Unpack Details pane: preview box resize ----------------------------------------------------

    private const double DetailsThumbMinHeight = 120, DetailsThumbMaxHeight = 900;

    /// <summary>Drags the preview box taller or shorter. Shared by the texture thumbnail and the model
    /// viewer - both want more room than the default on occasion.</summary>
    private void UnpackDetailThumbGrip_DragDelta(object sender,
        System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        UnpackDetailThumb.Height = Math.Clamp(UnpackDetailThumb.ActualHeight + e.VerticalChange,
            DetailsThumbMinHeight, DetailsThumbMaxHeight);
        // SizeChanged on the border already redraws the model at the new size.
    }

    private void UnpackDetailThumbGrip_DragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _settings.UnpackDetailsThumbHeight = UnpackDetailThumb.Height;
        _settings.Save();
    }

    /// <summary>Renders the current camera into the preview Image. Silent no-op when there is nothing
    /// to draw; a Vulkan error tears the preview down rather than repeating itself every frame.</summary>
    private void RenderModelPreview()
    {
        if (!_modelPreviewActive || _modelPreview is not { HasMesh: true } preview)
            return;

        // Render at device pixels and tag the bitmap with the matching DPI, so WPF lays it out at the
        // border's own size with one bitmap pixel per screen pixel (no resampling either way).
        var dpi = VisualTreeHelper.GetDpi(UnpackDetailThumb);
        int width = Quantize((UnpackDetailThumb.ActualWidth - 2) * dpi.DpiScaleX);
        int height = Quantize((UnpackDetailThumb.ActualHeight - 2) * dpi.DpiScaleY);
        if (width <= 0 || height <= 0)
            return;

        try
        {
            if (preview.Render(width, height, _modelYaw, _modelPitch, _modelZoom, _modelPan)
                is not { } pixels)
                return;

            if (_modelBitmap is null || _modelBitmap.PixelWidth != width || _modelBitmap.PixelHeight != height
                || Math.Abs(_modelBitmap.DpiX - 96 * dpi.DpiScaleX) > 0.01)
            {
                _modelBitmap = new WriteableBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
                    PixelFormats.Bgra32, null);
                UnpackDetailModel.Source = _modelBitmap;
            }
            _modelBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);

            UnpackDetailModelFps.Text =
                $"{preview.LastFrameMilliseconds:0.0} ms gpu  {_modelFps:0} fps  {width}x{height}";
        }
        catch (Exception ex)
        {
            // A lost device (driver reset, GPU removed) would otherwise throw on every mouse move.
            _vm.Unpack.LogPreview($"3D preview: render failed, disabling - {ex.Message}");
            _modelPreviewActive = false;
            _modelPreviewUnavailable = true;
            _modelPreview.Dispose();
            _modelPreview = null;
            UnpackDetailModel.Visibility = Visibility.Collapsed;
            UnpackDetailModelHint.Visibility = Visibility.Collapsed;
            UnpackDetailModelFps.Visibility = Visibility.Collapsed;
            UnpackDetailFallback.Visibility = Visibility.Visible;
            StopModelLoop();
        }

        // Device pixels, snapped down to a whole step and clamped. Snapping is what keeps a pane
        // resize from rebuilding the offscreen images on every single pixel of the drag.
        static int Quantize(double devicePixels)
        {
            int at = (int)devicePixels / ModelPreviewSizeStep * ModelPreviewSizeStep;
            return Math.Clamp(at, 0, ModelPreviewMaxPixels);
        }
    }

    private void UnpackDetailThumb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_modelPreviewActive)
            return;
        _modelDragging = true;
        _modelPanning = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        _modelDragFrom = e.GetPosition(UnpackDetailThumb);
        UnpackDetailThumb.CaptureMouse();
        UnpackDetailThumb.Cursor = Cursors.SizeAll;
    }

    /// <summary>Vertical half-angle of the render's field of view, as a tangent. Panning divides the
    /// drag by the pane height and scales by twice this, which is what makes the model track the
    /// cursor exactly rather than drifting ahead of or behind it.</summary>
    private const float ModelFovTangent = 0.41421357f; // tan(45 degrees / 2)

    private void UnpackDetailThumb_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_modelDragging || !_modelPreviewActive)
            return;
        var now = e.GetPosition(UnpackDetailThumb);
        double dx = now.X - _modelDragFrom.X, dy = now.Y - _modelDragFrom.Y;

        if (_modelPanning)
        {
            // Pan is measured against the un-zoomed framing distance, so the drag has to be scaled by
            // the zoom to keep the model glued to the cursor at every zoom level.
            double height = Math.Max(UnpackDetailThumb.ActualHeight, 1);
            float scale = (float)(2 * ModelFovTangent / height) * _modelZoom;
            // Negated so the model follows the cursor rather than running away from it.
            _modelPan.X -= (float)dx * scale;
            _modelPan.Y += (float)dy * scale;
        }
        else
        {
            _modelYaw -= (float)dx * 0.01f;
            _modelPitch = Math.Clamp(_modelPitch + (float)dy * 0.01f, -ModelMaxPitch, ModelMaxPitch);
        }

        _modelDragFrom = now;
        // No render here - the frame loop picks the new camera up on its next tick. Rendering inline
        // meant a fast drag queued one blocking submit per mouse event and the drag went sticky.
    }

    private void UnpackDetailThumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_modelDragging)
            return;
        _modelDragging = false;
        UnpackDetailThumb.ReleaseMouseCapture();
        UnpackDetailThumb.Cursor = null;
    }

    private void UnpackDetailThumb_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_modelPreviewActive)
            return;
        _modelZoom = Math.Clamp(_modelZoom * (e.Delta > 0 ? 0.88f : 1.0f / 0.88f), 0.15f, 6f);
        e.Handled = true; // don't scroll the Details pane out from under the model
    }

    /// <summary>Toggles the shared console window with the <c>~</c> / backtick key (like the Source
    /// engine console) from any tab. Ignored while a text field has focus, so the character can still
    /// be typed into titles, descriptions and paths.</summary>
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

    /// <summary>Darkens the OS title bar to match the app's theme. WPF doesn't theme the non-client
    /// area, so DWM is asked for the immersive dark title bar once the HWND exists.</summary>
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
    /// <summary>Short SHA-256 of the app assembly, shown in the Debug window title so local builds are
    /// distinguishable. Hashes PulseWorkshop.dll, not the thin apphost .exe, which stays constant
    /// across rebuilds. Null if the file can't be read.</summary>
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

    /// <summary>Enter in a search box flushes the pending (debounced) filter immediately instead of
    /// waiting for the typing-pause timer. The TextBox is matched to its list by x:Name.</summary>
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
        // Multi-select exists only for the context menu - a multi-row selection has no single item
        // to open, so leave the editor on whatever it was showing.
        var list = (ListBox)sender;
        if (list.SelectedItems.Count == 1 && list.SelectedItem is WorkshopItem item)
        {
            ClearOtherSelections(PublishedList);
            _vm.EditPublished(item);
        }
    }

    /// <summary>Right-clicking outside the highlighted rows moves the selection to the clicked row;
    /// right-clicking inside it keeps the whole multi-selection for the context menu.</summary>
    private void PublishedList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject origin
            || ItemsControl.ContainerFromElement(PublishedList, origin) is not ListBoxItem item)
            return;
        if (!item.IsSelected)
        {
            PublishedList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    /// <summary>Copies "title link" for every highlighted published item, one per line.</summary>
    private void CopyPublishedLinks_Click(object sender, RoutedEventArgs e)
    {
        var lines = PublishedList.SelectedItems.OfType<WorkshopItem>()
            .Select(i => $"{i.Title} {i.WorkshopUrl}")
            .ToList();
        if (lines.Count == 0)
            return;

        Clipboard.SetText(string.Join(Environment.NewLine, lines));
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

    /// <summary>Opens the Workshop item in the Steam client (steam://) so it appears in the overlay
    /// community; falls back to the default browser if Steam can't handle the protocol.</summary>
    private void OpenWorkshopItem_Click(object sender, RoutedEventArgs e)
    {
        if (RowData<WorkshopItem>(sender) is not { } item)
            return;

        var steamUrl = $"steam://url/CommunityFilePage/{item.PublishedFileId}";
        if (!TryOpen(steamUrl))
            TryOpen(item.WorkshopUrl); // browser fallback
    }

    /// <summary>Opens the official Steam "Add/Edit Images &amp; Videos" page for the item being edited.
    /// Gallery previews have no SDK API for these legacy Workshop games, so deep-link instead.</summary>
    private void OpenManagePreviews_Click(object sender, RoutedEventArgs e) =>
        OpenSteamItemPage("https://steamcommunity.com/sharedfiles/managepreviews/?id=");

    /// <summary>Opens the official Steam "Manage Required Items" page for the item being edited.</summary>
    private void OpenManageRequiredItems_Click(object sender, RoutedEventArgs e) =>
        OpenSteamItemPage("https://steamcommunity.com/workshop/managerequireditems/?id=");

    /// <summary>Deep-links to a Steam Workshop management page for the currently-edited published
    /// item, preferring the Steam client (steam://openurl) over the default browser.</summary>
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

    /// <summary>Selects a row in one of the lists - which opens it via the list's SelectionChanged
    /// handler - and scrolls it into view. Deferred to Background priority so a tab that was just
    /// switched to has realized its ListBox before we select and scroll.</summary>
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

    /// <summary>Clears the highlight in the other two lists (Published / Drafts / Templates), which
    /// each track their own. A stale selection left behind makes re-clicking that row a no-op -
    /// SelectionChanged never fires - and the editor keeps showing the wrong mode.</summary>
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

    /// <summary>Bulk publish/save: confirms first (spelling out how many are edits vs new items), then
    /// publishes every ticked draft. New-item drafts missing requirements are skipped and reported.</summary>
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

    /// <summary>Danger zone: permanently deletes the published item open in the editor. Confirms
    /// first, defaulting to "No", because the deletion is irreversible.</summary>
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

    /// <summary>Opens Crowbar (the inspiration for PulseWorkshop) on GitHub.</summary>
    private void OpenCrowbar_Click(object sender, RoutedEventArgs e) =>
        TryOpen(_vm.CrowbarUrl);

    /// <summary>Opens the Crowbar author's GitHub profile.</summary>
    private void OpenCrowbarAuthorGitHub_Click(object sender, RoutedEventArgs e) =>
        TryOpen(_vm.CrowbarAuthorGitHubUrl);

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
