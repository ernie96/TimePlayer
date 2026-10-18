using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Threading;
using TimePlayer.Core.Accessibility;
using Microsoft.Extensions.Logging;
using TimePlayer.Core.Configuration;
using TimePlayer.Core.Models;
using TimePlayer.Core.Updates;
using TimePlayer.Wpf.Accessibility;
using TimePlayer.Wpf.ViewModels;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// Main window: keyboard model, focus management, F1 help per area, F6 area cycling,
/// the settings dialog, the embedded mpv surface and the NVDA add-on's window messages.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IAccessibilityService _accessibility;
    private SettingsWindow? _settingsWindow;

    private readonly UpdateFlow _updateFlow;
    private readonly IConfigurationService _config;

    public MainWindow(MainViewModel viewModel, IAccessibilityService accessibility, IUpdateService updates,
        IConfigurationService config, ILogger<MainWindow> logger)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _accessibility = accessibility ?? throw new ArgumentNullException(nameof(accessibility));
        _config = config;
        _updateFlow = new UpdateFlow(updates, config, accessibility, logger);
        DataContext = _viewModel;

        if (_accessibility is WpfAccessibilityService wpfAccessibility)
        {
            wpfAccessibility.RegisterLiveRegion(LiveRegionStatusText);
        }

        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        MpvHost.HandleCreated += MpvHost_HandleCreated;

        _viewModel.RequestFocusSearch += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        });
        _viewModel.RequestFocusResults += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusCurrentList);
        _viewModel.RequestFocusPlayer += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PlayPauseButton.Focus());
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.RequestAddToPlaylistMenu += (_, item) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ShowAddToPlaylistMenu(item));
        _viewModel.Confirm = (title, message) =>
            MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        _viewModel.AskForText = (title, prompt, initial) => TextPromptWindow.Ask(this, title, prompt, initial);

        // Load more when the mouse wheel or scroll bar nears the end (keyboard users trigger it by selection).
        ResultsListBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (IsNearEnd(e)) _ = _viewModel.LoadMoreAsync();
        }));
        CommentsTree.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (IsNearEnd(e)) _ = _viewModel.LoadMoreCommentsAsync();
        }));

        // When comments arrive while the user waits on the Comments tab, move into the list.
        _viewModel.Comments.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && _viewModel.Comments.Count == e.NewItems?.Count
                && PlayerPanelTabs.IsKeyboardFocusWithin && _viewModel.SelectedPlayerPanel == PlayerPanel.Comments)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusCommentsTree);
            }
        };
    }

    private static bool IsNearEnd(ScrollChangedEventArgs e) =>
        e.ExtentHeight > 0 && e.VerticalChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 300;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.BrailleDisplayLine):
                Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    if (BrailleProgressTextBox.Text != _viewModel.BrailleDisplayLine)
                        BrailleProgressTextBox.Text = _viewModel.BrailleDisplayLine;
                });
                break;
            case nameof(MainViewModel.IsSettingsDialogOpen) when _viewModel.IsSettingsDialogOpen && _settingsWindow is null:
                Dispatcher.BeginInvoke(ShowSettingsDialog);
                break;
        }
    }

    private void ShowSettingsDialog()
    {
        if (_settingsWindow is not null)
            return;
        _settingsWindow = new SettingsWindow(_viewModel) { Owner = this };
        try
        {
            _settingsWindow.ShowDialog();
            if (_settingsWindow.PlayTestPatternRequested)
                _ = _viewModel.LoadTestVideoAsync();
            if (_settingsWindow.AppUpdateCheckRequested)
                _ = _updateFlow.RunAsync(this, manual: true);
        }
        finally
        {
            _settingsWindow = null;
            _viewModel.CloseSettings();
        }
    }

    private void MpvHost_HandleCreated(object? sender, EventArgs e)
    {
        if (MpvHost.WindowHandle != IntPtr.Zero)
        {
            _ = _viewModel.InitializePlayerEngineAsync(MpvHost.WindowHandle);
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (NvdaAddonInstaller.LastResult == NvdaAddonInstallResult.InstalledRestartRequired && NvdaBrailleBridge.IsNvdaRunning())
        {
            _viewModel.AnnounceNvdaAddonInstalled();
        }

        if (MpvHost.WindowHandle != IntPtr.Zero && !_viewModel.IsEngineInitialized)
        {
            _ = _viewModel.InitializePlayerEngineAsync(MpvHost.WindowHandle);
        }

        SearchBox.Focus();

        if (_viewModel.ShouldAskAboutBrailleAddon)
            Dispatcher.BeginInvoke(AskAboutBrailleAddon, DispatcherPriority.ApplicationIdle);

        // Check subscriptions after startup speech has finished.
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            await _viewModel.CheckFeedInBackgroundAsync();
            if (_config.CurrentConfig.Update.CheckAtStartup)
                await _updateFlow.RunAsync(this, manual: false);
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Asked once, and only when NVDA is installed; the answer is saved either way.</summary>
    private void AskAboutBrailleAddon()
    {
        var answer = MessageBox.Show(this,
            "Do you use a braille display with NVDA?\n\n" +
            "TimePlayer can add a small NVDA add-on that shows the playback position on your braille display " +
            "and lets the routing keys jump to any point in the video. NVDA needs to be restarted once after installing.\n\n" +
            "Choose Yes to install it, or No if you do not use braille. You will not be asked again; this can be changed in Settings.",
            "Braille display support", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        _ = _viewModel.ApplyBrailleAddonChoiceAsync(answer == MessageBoxResult.Yes);
        SearchBox.Focus();
    }

    // ===================== Focus helpers =====================

    private void FocusCurrentList()
    {
        if (_viewModel.IsPlayerDialogOpen)
            return;
        var list = _viewModel.IsQueueSection ? QueueListBox : ResultsListBox;
        if (list.Items.Count == 0)
        {
            SearchBox.Focus();
            return;
        }
        FocusListItem(list);
    }

    private static void FocusListItem(ListBox list)
    {
        if (list.Items.Count == 0)
        {
            list.Focus();
            return;
        }
        if (list.SelectedIndex < 0)
            list.SelectedIndex = 0;
        list.UpdateLayout();
        list.ScrollIntoView(list.SelectedItem);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item)
            item.Focus();
        else
            list.Focus();
    }

    private void FocusCommentsTree()
    {
        var node = _viewModel.SelectedComment ?? _viewModel.Comments.FirstOrDefault();
        if (node is null)
            return;
        CommentsTree.UpdateLayout();
        var container = CommentsTree.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem
            ?? CommentsTree.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
        if (container is null)
        {
            CommentsTree.Focus();
            return;
        }
        container.IsSelected = true;
        container.Focus();
    }

    private void CommentsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.SelectedComment = e.NewValue as CommentNode;

    /// <summary>Ctrl+D or the Download button: a short menu, so one key covers audio and video.</summary>
    private void ShowDownloadMenu(UIElement anchor, SearchResult? item)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        AutomationProperties.SetName(menu, "Download");
        void Add(string header, Action action, bool enabled = true)
        {
            var menuItem = new MenuItem { Header = header, IsEnabled = enabled };
            menuItem.Click += (_, _) => action();
            menu.Items.Add(menuItem);
        }
        bool isPlayer = item is null;
        Add(_viewModel.CanConvertDownloads ? "Download _audio (MP3)" : "Download _audio (M4A)",
            () => _ = isPlayer ? _viewModel.DownloadAudioCurrentAsync() : _viewModel.DownloadAudioForItemAsync(item));
        Add("Download _video (MP4)",
            () => _ = isPlayer ? _viewModel.DownloadVideoCurrentAsync() : _viewModel.DownloadVideoForItemAsync(item));
        Add("_Open downloads folder", _viewModel.OpenDownloadsFolder);
        if (_viewModel.HasActiveDownloads)
            Add("_Cancel downloads", _viewModel.CancelDownloads);
        menu.Opened += (_, _) => (menu.Items[0] as MenuItem)?.Focus();
        menu.IsOpen = true;
    }

    private void PlayerDownload_Click(object sender, RoutedEventArgs e) => ShowDownloadMenu(PlayerDownloadButton, null);

    /// <summary>Ctrl+P: your playlists, then "New playlist...". Liked videos has its own key, Ctrl+K.</summary>
    private void ShowAddToPlaylistMenu(SearchResult? item)
    {
        UIElement anchor = Keyboard.FocusedElement as UIElement ?? (UIElement)PlayerPlaylistButton;
        if (item is null && _viewModel.IsPlayerDialogOpen && !IsWithin(anchor as DependencyObject, PlayerPanelTabs))
            anchor = PlayerPlaylistButton;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        AutomationProperties.SetName(menu, "Add to playlist");
        foreach (var playlist in _viewModel.PlaylistsForMenu())
        {
            var id = playlist.Id;
            var menuItem = new MenuItem { Header = playlist.Name.Replace("_", "__") };
            menuItem.Click += (_, _) => _viewModel.AddToPlaylist(item, id);
            menu.Items.Add(menuItem);
        }
        var create = new MenuItem { Header = "_New playlist..." };
        create.Click += (_, _) => _viewModel.AddToPlaylist(item, null);
        menu.Items.Add(create);
        menu.Opened += (_, _) => (menu.Items[0] as MenuItem)?.Focus();
        menu.IsOpen = true;
    }

    private void ImportTakeout_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import from Google Takeout",
            Filter = "Google Takeout zip (*.zip)|*.zip|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            _ = _viewModel.ImportTakeoutAsync(dialog.FileName);
    }

    private void ShowPlayerPanel(PlayerPanel panel)
    {
        _viewModel.SelectedPlayerPanel = panel;
        // Loaded priority: after the tab has switched and laid out its content.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            switch (panel)
            {
                case PlayerPanel.UpNext when _viewModel.UpNext.Count > 0: FocusListItem(UpNextListBox); break;
                case PlayerPanel.Comments when _viewModel.Comments.Count > 0: FocusCommentsTree(); break;
                case PlayerPanel.Description: DescriptionBox.Focus(); break;
                case PlayerPanel.Queue when _viewModel.PlaybackQueue.Count > 0: FocusListItem(PlayerQueueListBox); break;
                default:
                    // Nothing listed yet: the tab takes focus, and its panel says why.
                    (PlayerPanelTabs.SelectedItem as TabItem)?.Focus();
                    break;
            }
        });
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject container)
    {
        while (element is not null)
        {
            if (element == container) return true;
            element = element is Visual
                ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>F6 / Shift+F6: move between the main areas of the current view.</summary>
    private void CycleArea(bool backwards)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        Action[] areas;
        int current;
        if (_viewModel.IsPlayerDialogOpen)
        {
            areas = new Action[]
            {
                () => PlayPauseButton.Focus(),
                () => ShowPlayerPanel(_viewModel.SelectedPlayerPanel),
                () => BackToSearchButton.Focus()
            };
            current = IsWithin(focused, PlayerPanelTabs) ? 1 : IsWithin(focused, BackToSearchButton) ? 2 : 0;
        }
        else
        {
            var list = _viewModel.IsQueueSection ? QueueListBox : ResultsListBox;
            areas = new Action[]
            {
                () => { SearchBox.Focus(); SearchBox.SelectAll(); },
                () => (SectionTabControl.ItemContainerGenerator.ContainerFromItem(SectionTabControl.SelectedItem) as TabItem)?.Focus(),
                () => { if (list.Items.Count > 0) FocusListItem(list); else SearchBox.Focus(); }
            };
            current = IsWithin(focused, SectionTabControl) ? 1 : IsWithin(focused, list) ? 2 : 0;
        }
        areas[(current + (backwards ? areas.Length - 1 : 1)) % areas.Length]();
    }

    // ===================== F1 help =====================

    private void ShowContextHelp()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        string title;
        string text;

        if (_viewModel.IsPlayerDialogOpen)
        {
            if (IsWithin(focused, UpNextListBox)) (title, text) = ("Up next", KeyboardHelp.UpNext);
            else if (IsWithin(focused, CommentsTree)) (title, text) = ("Comments", KeyboardHelp.Comments);
            else if (IsWithin(focused, DescriptionBox)) (title, text) = ("Description", KeyboardHelp.Description);
            else if (IsWithin(focused, PlayerQueueListBox)) (title, text) = ("Queue", KeyboardHelp.Queue);
            else (title, text) = ("Player", KeyboardHelp.Player);
        }
        else if (IsWithin(focused, SearchBox)) (title, text) = ("Search box", KeyboardHelp.SearchBox);
        else if (IsWithin(focused, SectionTabControl)) (title, text) = ("Sections", KeyboardHelp.Sections);
        else (title, text) = (_viewModel.SectionTitle, KeyboardHelp.ForList(_viewModel.CurrentSection));

        MessageBox.Show(this, text, $"Help: {title}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ===================== Keyboard =====================

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement;
        if (focused is MenuItem)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        if (key == Key.F1 && mods == ModifierKeys.None)
        {
            ShowContextHelp();
            e.Handled = true;
            return;
        }

        if (key == Key.F2 && mods == ModifierKeys.None)
        {
            _viewModel.OpenSettings();
            e.Handled = true;
            return;
        }

        if (key == Key.F6 && (mods == ModifierKeys.None || mods == ModifierKeys.Shift))
        {
            CycleArea(backwards: mods == ModifierKeys.Shift);
            e.Handled = true;
            return;
        }

        if (key == Key.D && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _viewModel.ToggleBrailleProgress();
            e.Handled = true;
            return;
        }

        e.Handled = _viewModel.IsPlayerDialogOpen
            ? HandlePlayerKey(key, mods, focused)
            : HandleLibraryKey(key, mods, focused);
    }

    private bool HandleLibraryKey(Key key, ModifierKeys mods, IInputElement? focused)
    {
        bool inTextBox = focused is TextBox;

        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            if (focused == SearchBox && !string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Clear();
                return true;
            }
            if (_viewModel.IsDetailView)
            {
                _ = _viewModel.GoBackAsync();
                return true;
            }
            return false;
        }

        if (mods == ModifierKeys.Control && SectionNumber(key) is int number)
        {
            _ = _viewModel.ShowSectionByNumberAsync(number);
            return true;
        }

        if (key == Key.Tab && (mods == ModifierKeys.Control || mods == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            int count = _viewModel.SectionTabs.Count;
            int current = _viewModel.SelectedSectionTab is { } tab ? _viewModel.SectionTabs.IndexOf(tab) : 0;
            int next = (current + (mods.HasFlag(ModifierKeys.Shift) ? count - 1 : 1)) % count;
            _ = _viewModel.ShowSectionByNumberAsync(next + 1);
            return true;
        }

        if (key == Key.D && mods == ModifierKeys.Control && focused is ListBoxItem { DataContext: SearchResult { IsVideo: true } item } listItem)
        {
            ShowDownloadMenu(listItem, item);
            return true;
        }

        if ((key == Key.E || key == Key.F) && mods == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            return true;
        }

        if (key == Key.F5 && mods == ModifierKeys.None)
        {
            _ = _viewModel.RefreshSectionAsync();
            return true;
        }

        if (_viewModel.IsDetailView &&
            ((key == Key.Left && mods == ModifierKeys.Alt) || (key == Key.Back && mods == ModifierKeys.None && !inTextBox)))
        {
            _ = _viewModel.GoBackAsync();
            return true;
        }

        // Your own playlists: Alt+Up and Alt+Down move the selected video.
        if (_viewModel.IsOpenPlaylistInLibrary && mods == ModifierKeys.Alt && (key == Key.Up || key == Key.Down) &&
            focused is ListBoxItem { DataContext: SearchResult })
        {
            _viewModel.MoveSelectedInPlaylist(key == Key.Up ? -1 : 1);
            return true;
        }

        return false;
    }

    private bool HandlePlayerKey(Key key, ModifierKeys mods, IInputElement? focused)
    {
        bool inSidePanel = focused is DependencyObject d && IsWithin(d, PlayerPanelTabs);

        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            // From a side panel, Escape returns to the controls; from the controls it closes the player.
            if (inSidePanel)
                PlayPauseButton.Focus();
            else
                _ = _viewModel.ClosePlayerDialogAsync();
            return true;
        }

        if (mods == ModifierKeys.Alt)
        {
            PlayerPanel? panel = key switch
            {
                Key.U => PlayerPanel.UpNext,
                Key.C => PlayerPanel.Comments,
                Key.D => PlayerPanel.Description,
                Key.Q => PlayerPanel.Queue,
                _ => null
            };
            if (panel is { } p)
            {
                ShowPlayerPanel(p);
                return true;
            }
            return false;
        }

        if (focused is TextBox { IsReadOnly: false })
            return false;

        bool inTextBox = focused is TextBox;   // the read-only description: arrows read its text
        bool isSlider = focused is Slider;
        bool isTree = focused is TreeView or TreeViewItem;
        bool isList = focused is ListBox or ListBoxItem || isTree;
        bool isComboBox = focused is ComboBox or ComboBoxItem;
        // In the comments tree, Left and Right open and close replies.
        bool arrowsBelongToControl = inTextBox || isSlider || isComboBox || isTree;
        // Space keeps activating the focused button, tab or check box.
        bool spaceBelongsToControl = focused is ButtonBase or TabItem || isComboBox || inTextBox;

        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C)
        {
            _viewModel.CopyShareLinkCurrent();
            return true;
        }

        if (mods == ModifierKeys.Control && key == Key.D)
        {
            if (focused is ListBoxItem { DataContext: SearchResult { IsVideo: true } upNextItem } upNextContainer)
                ShowDownloadMenu(upNextContainer, upNextItem);
            else
                ShowDownloadMenu(PlayerDownloadButton, null);
            return true;
        }

        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.C when !isList && focused is not TreeViewItem && !inTextBox: _viewModel.CopyShareLinkAtCurrentTime(); return true;
                case Key.S when !isList: _viewModel.ToggleSubscribeCurrentChannel(); return true;
                case Key.L when !isList: _viewModel.ToggleWatchLaterCurrent(); return true;
                case Key.K when !isList: _viewModel.ToggleLikeCurrent(); return true;
                case Key.P when !isList: _viewModel.ShowAddToPlaylistMenu(null); return true;
                case Key.G when !isList: _ = _viewModel.OpenCurrentChannelAsync(); return true;
                case Key.Left when !arrowsBelongToControl: _ = _viewModel.SeekLargeBackwardAsync(); return true;
                case Key.Right when !arrowsBelongToControl: _ = _viewModel.SeekLargeForwardAsync(); return true;
            }
            return false;
        }

        if (mods == ModifierKeys.Shift)
        {
            switch (key)
            {
                case Key.N: _ = _viewModel.PlayNextOrUpNextAsync(); return true;
                case Key.P: _ = _viewModel.PlayPreviousAsync(); return true;
                case Key.OemPeriod: _ = _viewModel.SpeedUpAsync(); return true;
                case Key.OemComma: _ = _viewModel.SpeedDownAsync(); return true;
            }
            return false;
        }

        if (mods != ModifierKeys.None)
            return false;

        if (DigitValue(key) is int digit && !inTextBox)
        {
            _ = _viewModel.SeekToPercentAsync(digit);
            return true;
        }

        switch (key)
        {
            case Key.Space when !spaceBelongsToControl:
            case Key.K:
                _ = _viewModel.TogglePlayPauseAsync();
                return true;
            case Key.M: _ = _viewModel.ToggleMuteAsync(); return true;
            case Key.T: _viewModel.AnnounceTime(); return true;
            case Key.I: _viewModel.AnnounceVideoInfo(); return true;
            case Key.U: _viewModel.AnnounceUpNext(); return true;
            case Key.A: _viewModel.ToggleAutoplay(); return true;
            case Key.C: _viewModel.ToggleSpeakCaptions(); return true;
            case Key.Home when !arrowsBelongToControl && !isList: _ = _viewModel.RestartFromBeginningAsync(); return true;
            case Key.Left when !arrowsBelongToControl: _ = _viewModel.SeekSmallBackwardAsync(); return true;
            case Key.Right when !arrowsBelongToControl: _ = _viewModel.SeekSmallForwardAsync(); return true;
            case Key.Up when !arrowsBelongToControl && !isList: _ = _viewModel.VolumeUpAsync(); return true;
            case Key.Down when !arrowsBelongToControl && !isList: _ = _viewModel.VolumeDownAsync(); return true;
        }
        return false;
    }

    private static int? SectionNumber(Key key) => DigitValue(key) is int d and >= 1 and <= 9 ? d : null;

    private static int? DigitValue(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => null
    };

    // ===================== Library toolbar =====================

    private void ImportSubscriptions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import subscriptions",
            Filter = "Subscription exports (Google Takeout zip or CSV, NewPipe, LibreTube, FreeTube, OPML)|*.zip;*.json;*.csv;*.opml;*.xml;*.db|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        _viewModel.ImportSubscriptionsFromFile(dialog.FileName);
    }

    private void YouTubeSubscriptionsHelp_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, KeyboardHelp.YouTubeSubscriptionsSteps, "Get your YouTube data",
            MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://takeout.google.com/settings/takeout/custom/youtube") { UseShellExecute = true });
        }
        catch (Exception)
        {
            _accessibility.Announce("Could not open the browser. Go to takeout.google.com yourself.", AnnouncementPriority.High);
        }
    }

    private void ExportSubscriptions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export subscriptions",
            FileName = $"timeplayer_subscriptions_{DateTime.Now:yyyyMMdd}.json",
            Filter = "NewPipe JSON, for NewPipe, LibreTube and Tubular (*.json)|*.json|OPML, for RSS readers and FreeTube (*.opml)|*.opml"
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.ExportSubscriptionsToFile(dialog.FileName);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Clear your whole watch history? Resume positions are lost too. This cannot be undone.",
            "Clear watch history", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            _viewModel.ClearHistory();
    }

    private void BrailleProgressTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && tb.ActualWidth > 0)
        {
            double fraction = Math.Clamp(e.GetPosition(tb).X / tb.ActualWidth, 0.0, 1.0);
            int cellIndex = (int)Math.Round(fraction * (_viewModel.SelectedBrailleCellCount - 1));
            _ = _viewModel.BrailleTouchCursorAsync(cellIndex);
            e.Handled = true;
        }
    }

    // ===================== NVDA add-on messages =====================

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);
    private const uint MSGFLT_ALLOW = 1;

    private static readonly string HwndFilePath = Path.Combine(Path.GetTempPath(), "timeplayer_hwnd.txt");

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is not HwndSource source)
            return;

        source.AddHook(WndProc);
        // Nothing is playing yet. Overwrite any state left by a previous run (for example after a
        // crash) before the add-on learns this window exists, so it does not show a lower g.
        NvdaBrailleBridge.PublishProgressState("stopped");
        ChangeWindowMessageFilterEx(source.Handle, (uint)NvdaAddonInstaller.WM_TIMEPLAYER_BRAILLE_TOUCH, MSGFLT_ALLOW, IntPtr.Zero);
        ChangeWindowMessageFilterEx(source.Handle, (uint)NvdaAddonInstaller.WM_TIMEPLAYER_TOGGLE_BRAILLE, MSGFLT_ALLOW, IntPtr.Zero);
        try
        {
            File.WriteAllText(HwndFilePath, source.Handle.ToInt64().ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Save the resume point and history while still on the UI thread.
        _ = _viewModel.DisposeAsync();
        try { File.Delete(HwndFilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        NvdaBrailleBridge.ClearProgressState();
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NvdaAddonInstaller.WM_TIMEPLAYER_BRAILLE_TOUCH)
        {
            // wParam = routing cell index, lParam = cell count of the display it came from.
            int cellIndex = wParam.ToInt32();
            int cellCount = lParam.ToInt32();
            if (cellIndex >= 0)
            {
                Dispatcher.BeginInvoke(() => _ = _viewModel.BrailleTouchCursorAsync(cellIndex, cellCount));
                handled = true;
            }
        }
        else if (msg == NvdaAddonInstaller.WM_TIMEPLAYER_TOGGLE_BRAILLE)
        {
            // Ctrl+Shift+D caught by the NVDA add-on, so it works wherever focus is.
            Dispatcher.BeginInvoke(_viewModel.ToggleBrailleProgress);
            handled = true;
        }
        return IntPtr.Zero;
    }
}
