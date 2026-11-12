using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LightBrowser;

public partial class MainWindow : Window
{
    readonly ProfileStore _store = ProfileStore.Load();
    readonly AppSettings _settings = AppSettings.Load();
    readonly SuggestionStore _suggestions = SuggestionStore.Load();
    readonly HistoryStore _history = new();
    readonly ICollectionView _view;
    CoreWebView2Environment? _env;
    readonly Dictionary<string, CoreWebView2Environment> _proxyEnvs = new();   // one engine per proxy in use
    readonly RequestBlocker _blocker = RequestBlocker.Load();
    Profile? _current;
    bool _suppressSelection;   // set while code (not the user) changes the selected profile
    bool _showTrash;           // sidebar is listing Trash instead of profiles
    bool _dirty;               // profiles.json has changes not yet written (e.g. last-used time)
    bool _recovering;          // rebuilding after the browser process crashed

    BrowserTab? ActiveTab => _current?.Active;
    CoreWebView2? ActiveWeb => ActiveTab?.View?.CoreWebView2;

    public MainWindow()
    {
        InitializeComponent();
        Ui.DarkTitleBar(this);

        _view = CollectionViewSource.GetDefaultView(_store.Profiles);
        _view.Filter = Matches;
        _view.SortDescriptions.Add(new SortDescription(nameof(Profile.GroupName), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(Profile.Name), ListSortDirection.Ascending));
        ProfileList.ItemsSource = _view;
        ApplySidebar();
        HighlightMode();
        UpdateBlockButton();
        UpdateSearchButton();
        Updater.CleanupOld(Environment.ProcessPath);   // the version this one replaced
        ShowUpdateIdle();
RefreshView();

        Closing += (_, _) =>
        {
            SaveSession();
            _history.Flush();
        };

        // Every 30 s: unload surplus/idle tabs, and save tab lists so a crash or power cut loses little.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            KeepSignInsOfLoadedProfiles();
            TrimMemory();
            AutoSave();
            _history.Flush();
        };
        timer.Start();

        Loaded += async (_, _) =>
        {
            try
            {
                _env = await CreateEnvironmentAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "The Microsoft Edge WebView2 Runtime could not start.\n\n" + ex.Message +
                    "\n\nInstall it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and reopen LightBrowser.",
                    "LightBrowser", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }
            var first = _view.Cast<Profile>().FirstOrDefault();
            if (first != null) ProfileList.SelectedItem = first;
            UpdateEmptyState();
            UpdateStats();
            _ = EnsureDesktopShortcutAsync();

            // Test hook: lets an end-to-end run check that an unhandled error is logged and survived.
            if (Environment.GetEnvironmentVariable("LIGHTBROWSER_TEST_THROW") == "1")
                _ = Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("test error from LIGHTBROWSER_TEST_THROW")));
        };
    }

    // ---- keeping sign-ins ------------------------------------------------------------------

    readonly HashSet<string> _keepPending = new();

    // Soon after a page finishes loading (a sign-in redirect has just set its cookie), once per profile at a time.
    void ScheduleKeepSignIns(Profile p, CoreWebView2 web)
    {
        if (!_settings.KeepSignIns || !_keepPending.Add(p.Id)) return;
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Dispatcher.InvokeAsync(async () =>
        {
            _keepPending.Remove(p.Id);
            await SignInKeeper.KeepAsync(web, _blocker.IsBlocked);
        }), TaskScheduler.Default);
    }

    // Logins made without a page load (a script sets the cookie) are caught by the 30-second timer.
    void KeepSignInsOfLoadedProfiles()
    {
        if (!_settings.KeepSignIns) return;
        foreach (var p in _store.Profiles)
            if (p.Tabs.Select(t => t.View?.CoreWebView2).FirstOrDefault(w => w != null) is { } web)
                _ = SignInKeeper.KeepAsync(web, _blocker.IsBlocked);
    }

    // The shortcut work runs off the UI thread so a slow desktop (network or synced folder) cannot delay the window.
    async Task EnsureDesktopShortcutAsync()
    {
        try
        {
            var offered = _settings.DesktopShortcutOffered;
            var exe = Environment.ProcessPath;
            var folder = DesktopShortcut.DesktopFolder();
            var result = await Task.Run(() => DesktopShortcut.Ensure(offered, exe, folder));
            if (result is DesktopShortcut.Outcome.Created or DesktopShortcut.Outcome.Present && !_settings.DesktopShortcutOffered)
            {
                _settings.DesktopShortcutOffered = true;
                _settings.Save();
            }
        }
        catch (Exception ex) { CrashLog.Write("desktop shortcut", ex); }
    }

    void UpdateEmptyState()
    {
        EmptyPanel.Visibility = _current == null ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.IsEnabled = _current != null;   // nothing to navigate until a profile is open
        EmptyNewBtn.Visibility = _showTrash ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Text = _showTrash
            ? "Trash\nRight-click a profile to restore it or delete it forever"
            : _store.Profiles.Any(p => !p.IsDeleted)
                ? "Select a profile on the left"
                : "Create a profile on the left to begin";
    }

    // Window title and the coloured bar above the tabs show which client profile is active.
    void ApplyProfileChrome()
    {
        Title = _current == null ? "LightBrowser" : $"{_current.Name} - LightBrowser";
        ProfileBar.Background = _current?.Brush ?? Brushes.Transparent;
        Application.Current.Resources["AccentBrush"] = _current?.Brush ?? new SolidColorBrush(Color.FromRgb(0x4F, 0x8E, 0xF7));

        // The toolbar chip shows the active profile while the left panel is hidden.
        ChipName.Text = _current?.Name ?? "Choose profile";
        ChipInitial.Text = _current?.Initial ?? "+";
        ChipDot.Fill = _current?.Brush ?? new SolidColorBrush(Color.FromRgb(0x6B, 0x6E, 0x78));
        UpdateSearchButton();   // the profile may use its own search engine
    }

    // ---- updates --------------------------------------------------------------------------

    ReleaseInfo? _update;
    bool _updating;

    void ShowUpdateIdle(string? note = null)
    {
        var current = $"v{Updater.CurrentVersion}";
        UpdateBtn.Content = _update != null ? $"Update to {_update.Tag}" : $"{current} - check for updates";
        UpdateBtn.Foreground = (Brush)FindResource(_update != null ? "AccentBrush" : "FaintBrush");
        UpdateBtn.FontWeight = _update != null ? FontWeights.SemiBold : FontWeights.Normal;
        UpdateBtn.ToolTip = note ?? (_update != null
            ? $"{_update.Tag} is available. Click to download and install it. Your profiles and logins are kept."
            : "Click to look for a newer version on GitHub. Nothing is contacted until you click.");
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            if (_update == null) await CheckForUpdateAsync();
            else await InstallUpdateAsync(_update);
        }
        finally { _updating = false; }
    }

    async Task CheckForUpdateAsync()
    {
        UpdateBtn.Content = "Checking...";
        try
        {
            var exe = Environment.ProcessPath;
            var latest = await Updater.CheckAsync(exe != null && Updater.IsStandalone(exe));
            if (latest != null && latest.Version > Updater.CurrentVersion) { _update = latest; ShowUpdateIdle(); }
            else
            {
                ShowUpdateIdle(latest == null ? "No release found. Click to check again." : "You have the latest version. Click to check again.");
                UpdateBtn.Content = $"v{Updater.CurrentVersion} - up to date";
            }
        }
        catch (Exception ex)
        {
            ShowUpdateIdle("Could not reach GitHub: " + ex.Message + "\nClick to try again.");
            UpdateBtn.Content = "Could not check - try again";
        }
    }

    async Task InstallUpdateAsync(ReleaseInfo release)
    {
        var exe = Environment.ProcessPath;
        // A development build, a read-only folder, or a release without a published checksum is updated by hand.
        if (!Updater.CanSelfUpdate(exe) || release.Sha256 == null)
        {
            if (release.PageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
            return;
        }
        var mb = release.Size > 0 ? $" ({release.Size / (1024 * 1024)} MB)" : "";
        if (MessageBox.Show(this,
                $"Update LightBrowser from v{Updater.CurrentVersion} to {release.Tag}?\n\nIt downloads {release.AssetName}{mb} from GitHub, checks it, and restarts the app. Your profiles, logins and settings are kept.",
                "Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var download = exe + ".new";
        try
        {
            var progress = new Progress<double>(p => UpdateBtn.Content = $"Downloading {p:P0}");
            UpdateBtn.Content = "Downloading 0%";
            await Updater.DownloadAsync(release, download, progress);
            UpdateBtn.Content = "Installing...";
            Updater.Swap(exe!, download);
        }
        catch (Exception ex)
        {
            try { System.IO.File.Delete(download); } catch (Exception) { }
            ShowUpdateIdle();
            MessageBox.Show(this, "The update was not installed, and the current version is unchanged.\n\n" + ex.Message,
                "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateBtn.Content = "Restarting...";
        Updater.RestartAfterExit(exe!);
        Application.Current.Shutdown();   // saves open tabs on the way out
    }

    void BlockToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.BlockTrackers = !_settings.BlockTrackers;
        _settings.Save();
        UpdateBlockButton();
        // YouTube ad blocking follows the same switch; pages already open apply it from their next load.
        foreach (var tab in _store.Profiles.SelectMany(p => p.Tabs).Where(t => t.View?.CoreWebView2 != null).ToList())
            _ = SetYouTubeAdBlockAsync(tab, tab.View!.CoreWebView2);
    }

    // Adds or removes the YouTube ad script for new pages in this tab, to match the Blocking switch.
    async Task SetYouTubeAdBlockAsync(BrowserTab tab, CoreWebView2 web)
    {
        try
        {
            if (_settings.BlockTrackers && tab.AdScriptId == null)
                tab.AdScriptId = await web.AddScriptToExecuteOnDocumentCreatedAsync(YouTubeAds.Script);
            else if (!_settings.BlockTrackers && tab.AdScriptId != null)
            {
                web.RemoveScriptToExecuteOnDocumentCreated(tab.AdScriptId);
                tab.AdScriptId = null;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("YouTube ad script", ex);   // blocking is best-effort; a failure must not stop the page
        }
    }

    // "Blocking 12" while on (the number is requests blocked since the app started), dim "Blocking off" otherwise.
    void UpdateBlockButton()
    {
        var on = _settings.BlockTrackers;
        BlockBtn.Content = on ? (_blocker.Blocked > 0 ? $"Blocking {_blocker.Blocked}" : "Blocking") : "Blocking off";
        BlockBtn.Foreground = (Brush)FindResource(on ? "TextBrush" : "FaintBrush");
        BlockBtn.ToolTip = on
            ? $"Ad and tracker blocking is on ({_blocker.Count} domains, {_blocker.Blocked} requests blocked), including YouTube ads. Click to turn off. Add your own domains in {RequestBlocker.FilePath}"
            : "Ad and tracker blocking is off. Click to turn on.";
    }

    // Back/forward enabled state and the loading bar follow the active tab.
    void UpdateChrome()
    {
        BackBtn.IsEnabled = ActiveWeb is { CanGoBack: true };
        ForwardBtn.IsEnabled = ActiveWeb is { CanGoForward: true };
        Loading.Visibility = ActiveTab is { IsLoading: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- saving ---------------------------------------------------------------------------

    static string? CurrentUrl(BrowserTab t)
    {
        try { return t.View?.CoreWebView2?.Source ?? t.LastUrl ?? t.PendingUrl; }
        catch (Exception) { return t.LastUrl ?? t.PendingUrl; }   // WebView2 already gone
    }

    // Remember each opened profile's tabs so they come back next time. True if the list changed.
    static bool CaptureSession(Profile p)
    {
        if (p.Tabs.Count == 0) return false;   // not opened this run: keep what was saved before
        var urls = p.Tabs.Select(CurrentUrl).Where(s => !string.IsNullOrEmpty(s) && s != "about:blank").Select(s => s!).ToList();
        if (urls.SequenceEqual(p.OpenUrls)) return false;
        p.OpenUrls = urls;
        return true;
    }

    void SaveSession()
    {
        foreach (var p in _store.Profiles) CaptureSession(p);
        _store.Save();
        _dirty = false;
    }

    // Used by the crash handler: saves the open tabs, and never throws.
    public void SaveSessionSafely()
    {
        try { SaveSession(); }
        catch (Exception ex) { CrashLog.Write("saving the session after an error", ex); }
    }

    void AutoSave()
    {
        var changed = _dirty;
        foreach (var p in _store.Profiles) changed |= CaptureSession(p);
        if (!changed) return;
        _store.Save();
        _dirty = false;
    }

    // ---- profile list: search, groups, trash ----------------------------------------------

    bool Matches(object item)
    {
        var p = (Profile)item;
        if (p.IsDeleted != _showTrash) return false;
        var q = SearchBox.Text.Trim();
        return q.Length == 0
            || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Group.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Notes.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // Re-apply filter, sort and grouping. Group headings only appear once any profile has a group.
    void RefreshView()
    {
        _suppressSelection = true;
        try
        {
            var grouped = !_showTrash && _store.Profiles.Any(p => !p.IsDeleted && !string.IsNullOrWhiteSpace(p.Group));
            if (grouped != _view.GroupDescriptions.Count > 0)
            {
                _view.GroupDescriptions.Clear();
                if (grouped) _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Profile.GroupName)));
            }
            _view.Refresh();
            if (!_showTrash && _current is { IsDeleted: false } c && _view.Contains(c))
                ProfileList.SelectedItem = c;

            var trashed = _store.Profiles.Count(p => p.IsDeleted);
            TrashBtn.Content = _showTrash ? "< Back to profiles" : $"Trash ({trashed})";
        }
        finally { _suppressSelection = false; }
    }

    string CanonicalGroup(string group)
    {
        group = group.Trim();
        if (group == "") return "";
        return _store.Profiles.Select(p => p.Group)
            .FirstOrDefault(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase)) ?? group;
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshView();
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (!_showTrash && _view.Cast<Profile>().FirstOrDefault() is { } first) ProfileList.SelectedItem = first;
            e.Handled = true;
        }
    }

    void Trash_Click(object sender, RoutedEventArgs e)
    {
        _showTrash = !_showTrash;
        SelectNoProfile();
        RefreshView();
    }

    void ProfileMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not Profile p) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var show = item.Tag switch
            {
                "trash" => p.IsDeleted,
                "open" => !p.IsDeleted && p.IsOpen,
                _ => !p.IsDeleted,
            };
            item.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---- profiles -------------------------------------------------------------------------

    async void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_env == null || _suppressSelection || _showTrash) return;
        if (ProfileList.SelectedItem is Profile p) await ShowProfileAsync(p, openPages: true);
        else ShowNothing();
    }

    void SelectProfileQuietly(Profile p)
    {
        _suppressSelection = true;
        ProfileList.SelectedItem = p;
        _suppressSelection = false;
    }

    void SelectNoProfile()
    {
        _suppressSelection = true;
        ProfileList.SelectedItem = null;
        _suppressSelection = false;
        ShowNothing();
    }

    void ShowNothing()
    {
        _current = null;
        TabList.ItemsSource = null;
        Address.Text = "";
        HideAllViews();
        ApplyProfileChrome();
        UpdateChrome();
        UpdateEmptyState();
    }

    void HideAllViews()
    {
        foreach (var pr in _store.Profiles)
            foreach (var t in pr.Tabs)
                if (t.View != null)
                {
                    if (t.View.Visibility == Visibility.Visible) t.LastShown = DateTime.UtcNow;
                    t.View.Visibility = Visibility.Collapsed;
                    SetMemoryLevel(t, visible: false);
                }
    }

    async Task ShowProfileAsync(Profile p, bool openPages)
    {
        _current = p;
        HideAllViews();
        TabList.ItemsSource = p.Tabs;
        ApplyProfileChrome();
        UpdateEmptyState();
        p.LastUsed = DateTime.UtcNow;
        _dirty = true;

        if (p.Tabs.Count > 0) SelectTab(p.Active ?? p.Tabs[0]);
        else if (openPages) await OpenStartPagesAsync(p);
        else UpdateChrome();
    }

    // First open of a profile in this run: restore last session, else its home pages, else a blank tab.
    // Only the first restored tab is loaded; the rest stay as titles and load when clicked, to save RAM.
    async Task OpenStartPagesAsync(Profile p)
    {
        if (p.OpenUrls.Count == 0)
        {
            await OpenHomeAsync(p);
            return;
        }
        var urls = p.OpenUrls.ToList();
        var first = NewTabAsync(p, urls[0]);   // adds its tab synchronously, so order is kept
        foreach (var url in urls.Skip(1)) AddLazyTab(p, url);
        await first;
    }

    static BrowserTab AddLazyTab(Profile p, string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        var tab = new BrowserTab { PendingUrl = url, LastUrl = url, Title = host };
        p.Tabs.Add(tab);
        return tab;
    }

    async Task OpenHomeAsync(Profile p)
    {
        if (p.HomeUrls.Count == 0)
        {
            await NewTabAsync(p, "about:blank");
            Address.Focus();
            return;
        }
        var first = p.Tabs.Count;
        foreach (var url in p.HomeUrls) await NewTabAsync(p, url);
        SelectTab(p.Tabs[first]);
    }

    static Profile? ProfileOf(object sender) => (sender as FrameworkElement)?.DataContext as Profile;

    void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ProfileDialog(null, _suggestions.Sites, GroupSitesFor(null), ExistingNames(null), _settings.SearchEngine) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var p = new Profile
        {
            Name = dlg.ProfileName, Color = dlg.ColorHex, HomeUrls = dlg.HomeUrls,
            Group = CanonicalGroup(dlg.GroupName), Notes = dlg.Notes,
            Proxy = dlg.Proxy, ProxyUser = dlg.ProxyUser, SearchEngine = dlg.SearchEngineId,
        };
        p.ProxyPassword = dlg.ProxyPassword;
        _store.Profiles.Add(p);
        _store.Save();

        _showTrash = false;
        SearchBox.Clear();      // so the new profile is not filtered out
        RefreshView();
        ProfileList.SelectedItem = p;
    }

    async void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p) return;
        var dlg = new ProfileDialog(p, _suggestions.Sites, GroupSitesFor(p), ExistingNames(p), _settings.SearchEngine) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // A profile's logins live in the engine of its proxy, so a different proxy means a clean store.
        // The old store is wiped first so no sign-ins are left behind, and the tabs reopen through the new proxy.
        var reopen = false;
        if (dlg.Proxy != p.Proxy && _env != null)
        {
            var ok = MessageBox.Show(this,
                $"Changing the proxy of \"{p.Name}\" signs it out of every website, because its cookies stay with the proxy they were made through.\n\n" +
                "Saved passwords in the password list are kept. Continue?",
                "Change proxy", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.Yes) return;
            reopen = p == _current;
            try { await WipeProfileDataAsync(p); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not clear the profile's old sign-ins, so the proxy was not changed.\n\n" + ex.Message,
                    "Change proxy", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            CloseProfile(p);
            p.Proxy = dlg.Proxy;
        }
        p.ProxyUser = dlg.ProxyUser;
        p.ProxyPassword = dlg.ProxyPassword;
        p.SearchEngine = dlg.SearchEngineId;
        p.Name = dlg.ProfileName;
        p.Color = dlg.ColorHex;
        p.HomeUrls = dlg.HomeUrls;
        p.Group = CanonicalGroup(dlg.GroupName);
        p.Notes = dlg.Notes;
        _store.Save();
        if (p == _current) ApplyProfileChrome();
        RefreshView();
        if (reopen) ProfileList.SelectedItem = p;   // reopens its tabs through the new proxy
    }

    Func<string, IReadOnlyList<string>> GroupSitesFor(Profile? except) =>
        group => SuggestionStore.ForGroup(group, _store.Profiles, except);

    void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p) return;
        _store.Profiles.Add(p.Duplicate(ExistingNames(null)));
        _store.Save();
        RefreshView();
    }

    async void OpenHome_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p || _env == null) return;
        SelectProfileQuietly(p);
        await ShowProfileAsync(p, openPages: false);
        await OpenHomeAsync(p);
    }

    // Closes every tab of a profile to free its memory. Logins stay saved; the tab list is
    // remembered and comes back (lazily) the next time the profile is opened.
    void CloseProfile(Profile p)
    {
        CaptureSession(p);
        foreach (var t in p.Tabs.ToList()) DisposeTab(p, t);
        p.Active = null;
        if (p == _current) SelectNoProfile();
        _store.Save();
    }

    void CloseProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } p) CloseProfile(p);
    }

    void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p) return;
        CloseProfile(p);
        ProfileList.SelectedItem = p;   // reopens it from its saved tab list
    }

    // Wipes cookies, logins and site data of one profile; other profiles are untouched. Needs a live
    // WebView2 on that profile, so a blank tab is opened for it if none is loaded.
    async Task WipeProfileDataAsync(Profile p)
    {
        SelectProfileQuietly(p);
        await ShowProfileAsync(p, openPages: false);
        var live = p.Tabs.FirstOrDefault(t => t.View?.CoreWebView2 != null);
        var temp = live == null ? await NewTabAsync(p, "about:blank") : null;
        await (live ?? temp)!.View!.CoreWebView2.Profile.ClearBrowsingDataAsync();
        if (temp != null) DisposeTab(p, temp);
    }

    async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p || _env == null) return;
        var ok = MessageBox.Show(this,
            $"Clear all cookies, logins and site data of \"{p.Name}\"?\n\nYou will be signed out of every site in this profile. Other profiles are not affected.",
            "Clear website data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        await WipeProfileDataAsync(p);
        foreach (var t in p.Tabs) t.View?.CoreWebView2?.Reload();
        if (p.Tabs.Count == 0) SelectNoProfile();
    }

    void MoveToTrash_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p) return;
        var ok = MessageBox.Show(this,
            $"Move \"{p.Name}\" to Trash?\n\nIts logins stay saved until you delete it forever from Trash.",
            "Move to Trash", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        CloseProfile(p);
        p.IsDeleted = true;
        _store.Save();
        RefreshView();
        UpdateEmptyState();
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p) return;
        p.IsDeleted = false;
        _store.Save();
        RefreshView();
    }

    async void DeleteForever_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p || _env == null) return;
        var ok = MessageBox.Show(this,
            $"Delete \"{p.Name}\" forever and wipe its cookies, logins and site data?\n\nThis cannot be undone.",
            "Delete forever", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        foreach (var lazy in p.Tabs.Where(t => t.View == null).ToList()) p.Tabs.Remove(lazy);
        await WipeProfileDataAsync(p);
        foreach (var t in p.Tabs.ToList()) DisposeTab(p, t);
        _current = null;
        _history.Clear(p.Id);
        PasswordVault.Delete(p.Id);
        _store.Profiles.Remove(p);
        _store.Save();
        ShowNothing();
        RefreshView();
    }

    // ---- launch one site in several profiles ----------------------------------------------

    IEnumerable<string> ExistingNames(Profile? except) => _store.Profiles.Where(p => p != except).Select(p => p.Name);

    // ---- rename, history, passwords -------------------------------------------------------

    void ProfileRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ProfileOf(sender) is { IsDeleted: false } p)
        {
            Rename(p);
            e.Handled = true;
        }
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } p) Rename(p);
    }

    void Rename(Profile p)
    {
        if (TextPrompt.Ask(this, "Rename profile", "New name", p.Name) is not { Length: > 0 } name || name == p.Name) return;
        p.Name = name;
        _store.Save();
        if (p == _current) ApplyProfileChrome();
        RefreshView();
    }

    async void History_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } p) await ShowHistoryAsync(p);
    }

    async void HistoryButton_Click(object sender, RoutedEventArgs e) => await ShowHistoryAsync(_current);

    async Task ShowHistoryAsync(Profile? p)
    {
        if (p == null || _env == null) return;
        var dlg = new HistoryDialog(p.Name, p.Id, _history) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.OpenUrl is not { } url) return;
        await OpenInProfileAsync(p, url);
    }

    // Makes p the profile on screen, then opens url in a new tab of it.
    async Task OpenInProfileAsync(Profile p, string url)
    {
        if (_current != p)
        {
            SelectProfileQuietly(p);
            await ShowProfileAsync(p, openPages: false);
        }
        await NewTabAsync(p, url);
    }

    // This profile's own password list. "Fill page" runs on the tab on screen, so the profile is brought
    // forward first.
    async void Passwords_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is { } p) await ShowVaultAsync(p);
    }

    async void PasswordsButton_Click(object sender, RoutedEventArgs e) => await ShowVaultAsync(_current);

    async Task ShowVaultAsync(Profile? p)
    {
        if (p == null || _env == null) return;
        if (_current != p)
        {
            SelectProfileQuietly(p);
            await ShowProfileAsync(p, openPages: false);
        }
        var web = ActiveWeb;
        string? url = null;
        try { url = web?.Source; } catch (Exception) { }
        var dlg = new VaultDialog(p.Name, p.Id, url, canFill: web != null && SuggestionStore.Normalize(url) != null) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.FillEntry is not { } entry || ActiveWeb is not { } target) return;

        var result = await target.ExecuteScriptAsync(PasswordVault.FillScript(entry.Username, entry.Password));
        if (result.Contains("no-password-field"))
            MessageBox.Show(this, "No login form was found on this page. Open the login page first (the form must be on the main page, not inside a frame).",
                "Fill page", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_env == null) return;
        var profiles = _store.Profiles.Where(p => !p.IsDeleted)
            .OrderBy(p => p.GroupName).ThenBy(p => p.Name).ToList();
        var dlg = new LaunchDialog(profiles, _suggestions.Sites, _current) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        if (dlg.Action == LaunchAction.Close)
        {
            foreach (var p in dlg.Selected) CloseProfile(p);
            return;
        }
        foreach (var p in dlg.Selected) await OpenSiteInProfileAsync(p, dlg.Url);
    }

    static bool SameUrl(string? a, string? b) =>
        string.Equals(a?.TrimEnd('/'), b?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    // The profile on screen gets a loaded tab. Any other profile gets an unloaded tab that loads when
    // you open that profile, so launching in 50 profiles does not load 50 pages.
    async Task OpenSiteInProfileAsync(Profile p, string url)
    {
        if (p.Tabs.Count == 0)   // keep its saved tabs when it has not been opened this run
            foreach (var saved in p.OpenUrls) AddLazyTab(p, saved);

        var existing = p.Tabs.FirstOrDefault(t => SameUrl(CurrentUrl(t), url));
        if (p == _current)
        {
            if (existing != null) SelectTab(existing);
            else await NewTabAsync(p, url);
            return;
        }
        p.Active = existing ?? AddLazyTab(p, url);
    }

    // ---- tabs -----------------------------------------------------------------------------

    async Task<BrowserTab> NewTabAsync(Profile p, string? url)
    {
        var tab = new BrowserTab();
        p.Tabs.Add(tab);
        await LoadTabAsync(p, tab, url);
        return tab;
    }

    // Creates the WebView2 for a tab (new, or a restored one being opened for the first time).
    async Task LoadTabAsync(Profile p, BrowserTab tab, string? url)
    {
        CoreWebView2Environment env;
        try { env = await EnvForAsync(p); }
        catch (Exception ex)
        {
            tab.PendingUrl = url;   // stays an unloaded tab; nothing is sent
            MessageBox.Show(this, $"The proxy engine for \"{p.Name}\" could not start, so the page was not opened.\n\n{ex.Message}",
                "Proxy", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var view = new WebView2();
        tab.View = view;
        tab.PendingUrl = null;
        WebHost.Children.Add(view);
        if (_current == p) SelectTab(tab);   // must be visible before WebView2 can create its window

        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = p.WebViewProfileName;
        options.IsInPrivateModeEnabled = false;
        await view.EnsureCoreWebView2Async(env, options);
        var web = view.CoreWebView2;

        // Answer the proxy's own sign-in request (never a website's) with this profile's proxy login.
        if (p.ProxySpec is { } proxySpec && p.ProxyUser != "")
            web.BasicAuthenticationRequested += (_, args) =>
            {
                if (!proxySpec.IsChallengeFrom(args.Uri)) return;
                args.Response.UserName = p.ProxyUser;
                args.Response.Password = p.ProxyPassword;
            };
        // Ad and tracker requests get an empty 403 before they leave the browser (no-op while blocking is off).
        web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        web.WebResourceRequested += (_, args) =>
        {
            if (!_settings.BlockTrackers || !_blocker.IsBlocked(args.Request.Uri)) return;
            _blocker.Blocked++;
            UpdateBlockButton();
            args.Response = env.CreateWebResourceResponse(
                new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("Blocked by LightBrowser")), 403, "Blocked", "Content-Type: text/plain");
        };
        web.Settings.IsPasswordAutosaveEnabled = true;   // "Save password?" prompt, stored per profile
        web.Settings.IsGeneralAutofillEnabled = true;
        SetMemoryLevel(tab, visible: ActiveTab == tab);

        web.FaviconChanged += async (_, _) => await UpdateFaviconAsync(tab, web);
        web.DocumentTitleChanged += (_, _) => _history.UpdateTitle(p.Id, web.Source, web.DocumentTitle);
        web.DocumentTitleChanged += (_, _) =>
            tab.Title = string.IsNullOrWhiteSpace(web.DocumentTitle) ? web.Source : web.DocumentTitle;
        web.SourceChanged += (_, _) =>
        {
            tab.LastUrl = web.Source;
            if (ActiveTab == tab) Address.Text = web.Source == "about:blank" ? "" : web.Source;
        };
        web.NavigationStarting += (_, _) =>
        {
            tab.IsLoading = true;
            if (ActiveTab == tab) UpdateChrome();
        };
        web.NavigationCompleted += (_, args) =>
        {
            tab.IsLoading = false;
            if (args.IsSuccess) tab.Crashes = 0;
            if (ActiveTab == tab) UpdateChrome();
            // A page that loaded with a 2xx status is a site that works: remember it for every profile.
            _suggestions.AddIfVerified(web.Source, args.IsSuccess, args.HttpStatusCode);
            if (args.IsSuccess) _history.Record(p.Id, web.Source, web.DocumentTitle);
            if (args.IsSuccess) ScheduleKeepSignIns(p, web);
        };
        web.HistoryChanged += (_, _) => { if (ActiveTab == tab) UpdateChrome(); };

        // Crash recovery. A crashed page renderer reloads its tab (at most 3 times in a row); a crashed
        // browser process takes every WebView2 down, so all tabs are rebuilt from their last address.
        web.ProcessFailed += (_, args) =>
        {
            switch (args.ProcessFailedKind)
            {
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    _ = RecoverFromBrowserCrashAsync();
                    break;
                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    if (++tab.Crashes <= 3) web.Reload();
                    break;
            }
        };

        // Popups (OAuth, "pay with" windows) open as tabs in the same profile and keep window.opener.
        web.NewWindowRequested += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            // A Ctrl/middle-click on a link opens its tab in the background, as in other browsers. The page tells us
            // about the click (a moment before or after this event), so give that message a moment to arrive.
            await Task.Delay(60);
            var background = DateTime.UtcNow < _openInBackgroundUntil && _current == p;
            var previous = ActiveTab;
            if (_current != p) SelectProfileQuietly(p);
            if (_current != p) await ShowProfileAsync(p, openPages: false);
            var popup = await NewTabAsync(p, null);
            if (popup.View?.CoreWebView2 is { } popupWeb) args.NewWindow = popupWeb;
            else args.Handled = true;   // its engine could not start: no window, and nothing was sent
            if (background && previous != null && previous != popup && p.Tabs.Contains(previous) && previous.View != null)
            {
                popup.ProtectedUntil = DateTime.UtcNow.AddSeconds(15);
                _openInBackgroundUntil = DateTime.MinValue;
                SelectTab(previous);
            }
            deferral.Complete();
        };
        web.ContextMenuRequested += (_, args) => AddContextMenuItems(p, web, args);
        // window.close() from a popup (e.g. a login window finishing) closes its tab.
        web.WindowCloseRequested += (_, _) => Dispatcher.InvokeAsync(() => CloseTabAsync(tab, remember: false));

        await SetYouTubeAdBlockAsync(tab, web);

        // The WPF control hides the controller's key events, so the page reports Ctrl+T/W/L/K itself.
        await web.AddScriptToExecuteOnDocumentCreatedAsync(ShortcutScript);
        web.WebMessageReceived += (_, args) =>
        {
            // Only the page itself may trigger a shortcut, not an embedded frame from another site.
            if (!SameOrigin(args.Source, web.Source)) return;
            var msg = args.TryGetWebMessageAsString();
            if (msg == "o") { _openInBackgroundUntil = DateTime.UtcNow.AddSeconds(1); return; }
            if (msg is { Length: 1 }) HandleShortcut(msg[0]);
        };

        if (url != null && url != "about:blank") web.Navigate(url);
        TrimMemory();
    }

    // The engine for profiles without a proxy (proxy == null), or the separate engine for one proxy.
    // Low RAM starts the engine without GPU acceleration (about 25% less memory); it applies from the next start.
    Task<CoreWebView2Environment> CreateEnvironmentAsync(ProxySpec? proxy = null)
    {
        var args = ProxySpec.EngineArguments(proxy, _settings);
        var options = args == "" ? null : new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = args };
        return CoreWebView2Environment.CreateAsync(null, ProfileStore.DataDirFor(proxy), options);
    }

    // A profile with a proxy never falls back to the default engine: if its own engine cannot start, it throws,
    // so the profile's traffic cannot go out directly.
    async Task<CoreWebView2Environment> EnvForAsync(Profile p)
    {
        if (p.ProxySpec is not { } proxy) return _env!;
        if (_proxyEnvs.TryGetValue(proxy.Server, out var known)) return known;
        var created = await CreateEnvironmentAsync(proxy);
        _proxyEnvs[proxy.Server] = created;
        return created;
    }

    // Shows the site's icon on its tab (a small 32 px copy, so it costs almost no memory).
    static async Task UpdateFaviconAsync(BrowserTab tab, CoreWebView2 web)
    {
        try
        {
            using var stream = await web.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null) { tab.Favicon = null; return; }
            using var copy = new System.IO.MemoryStream();   // the engine's stream is not seekable
            await stream.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            tab.Favicon = image;
        }
        catch (Exception)
        {
            // The tab was closed meanwhile, or the icon could not be decoded: keep the default globe.
        }
    }

    static bool SameOrigin(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && x.GetLeftPart(UriPartial.Authority) == y.GetLeftPart(UriPartial.Authority);

    async Task RecoverFromBrowserCrashAsync()
    {
        if (_recovering) return;
        _recovering = true;
        try
        {
            foreach (var tab in _store.Profiles.SelectMany(p => p.Tabs).Where(t => t.View != null).ToList())
            {
                var url = CurrentUrl(tab);
                tab.PendingUrl = url == "about:blank" ? null : url;
                WebHost.Children.Remove(tab.View!);
                try { tab.View!.Dispose(); } catch (Exception) { }
                tab.View = null;
                tab.IsLoading = false;
            }
            _proxyEnvs.Clear();   // proxy engines are started again, when a tab needs one
            _env = await CreateEnvironmentAsync();
            if (_current?.Active is { } active) SelectTab(active);   // reload what was on screen
        }
        catch (Exception)
        {
            // Tabs stay unloaded; clicking one tries again.
        }
        finally { _recovering = false; }
    }

    void SelectTab(BrowserTab tab)
    {
        if (_current == null) return;
        foreach (var t in _current.Tabs)
            if (t.View != null)
            {
                if (t != tab && t.View.Visibility == Visibility.Visible) t.LastShown = DateTime.UtcNow;
                t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
                SetMemoryLevel(t, visible: t == tab);
            }
        tab.LastShown = DateTime.UtcNow;
        _current.Active = tab;
        TabList.SelectedItem = tab;
        TabList.ScrollIntoView(tab);

        if (tab.View == null)   // restored tab clicked for the first time: open it now
        {
            Address.Text = tab.PendingUrl ?? "";
            UpdateChrome();
            _ = LoadTabAsync(_current, tab, tab.PendingUrl);
            return;
        }
        var src = tab.View.CoreWebView2?.Source;
        Address.Text = src == null || src == "about:blank" ? "" : src;
        UpdateChrome();
    }

    void DisposeTab(Profile p, BrowserTab tab)
    {
        p.Tabs.Remove(tab);
        if (tab.View != null) RetireView(p, tab.View);
        if (p.Active == tab) p.Active = null;
        ReleaseUnusedProxyEngines();
    }

    // Takes a tab's page out of the window and disposes it. When it was the profile's last loaded page, its session
    // cookies are given an expiry first, or the profile would be signed out of everything (see SignInKeeper).
    void RetireView(Profile p, WebView2 view)
    {
        WebHost.Children.Remove(view);
        var web = view.CoreWebView2;
        if (_settings.KeepSignIns && web != null && !p.Tabs.Any(t => t.View != null))
            _ = KeepThenDisposeAsync(view, web);
        else
            view.Dispose();
    }

    async Task KeepThenDisposeAsync(WebView2 view, CoreWebView2 web)
    {
        try { await SignInKeeper.KeepAsync(web, _blocker.IsBlocked); }
        finally { view.Dispose(); }
    }

    // A proxy's browser engine only runs while some tab uses it. When the last loaded tab of every profile on a
    // proxy is gone, the engine is let go so its browser processes exit and free their memory. It starts again
    // the next time a tab on that proxy loads.
    bool _gcQueued;

    void ReleaseUnusedProxyEngines()
    {
        if (_proxyEnvs.Count == 0) return;
        var inUse = _store.Profiles
            .Where(p => p.ProxySpec != null && p.Tabs.Any(t => t.View != null))
            .Select(p => p.ProxySpec!.Server)
            .ToHashSet();
        var unused = _proxyEnvs.Keys.Where(k => !inUse.Contains(k)).ToList();
        if (unused.Count == 0) return;
        foreach (var key in unused) _proxyEnvs.Remove(key);

        if (_gcQueued) return;
        _gcQueued = true;   // the engine's handle is released when the .NET wrapper around it is collected
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            _gcQueued = false;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    async Task CloseTabAsync(BrowserTab tab, bool remember = true)
    {
        var p = _store.Profiles.FirstOrDefault(x => x.Tabs.Contains(tab));
        if (p == null) return;
        var index = p.Tabs.IndexOf(tab);
        if (remember) RememberClosedTab(p, tab);
        DisposeTab(p, tab);
        if (p != _current) return;

        if (p.Tabs.Count == 0)
        {
            await NewTabAsync(p, "about:blank");
            Address.Focus();
        }
        else SelectTab(p.Tabs[Math.Min(index, p.Tabs.Count - 1)]);
    }

    void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabList.SelectedItem is BrowserTab t && t != ActiveTab) SelectTab(t);
    }

    // Middle-click on a tab closes it.
    async void TabList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not BrowserTab tab) return;
        e.Handled = true;
        await CloseTabAsync(tab);
    }

    // Double-click on the empty part of the tab bar opens a new tab (as in other browsers).
    async void TabList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(TabList, source) != null) return;   // on a tab, not the empty space
        e.Handled = true;
        await StartNewTabAsync();
    }

    async void NewTab_Click(object sender, RoutedEventArgs e) => await StartNewTabAsync();

    async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) await CloseTabAsync(tab);
    }

    async Task StartNewTabAsync()
    {
        if (_current is not { } p) return;
        await NewTabAsync(p, "about:blank");
        Address.Focus();
    }

    // ---- memory ---------------------------------------------------------------------------

    // Keeps RAM bounded however many tabs exist: only MaxLoadedTabs tabs are ever loaded, and a hidden
    // tab unused for IdleMinutes is dropped. A dropped tab keeps its address and title, costs almost
    // nothing, and reloads (still logged in, cookies are on disk) when clicked.
    void TrimMemory(bool ignoreGrace = false)
    {
        var now = DateTime.UtcNow;
        var limits = _settings.Limits;
        var grace = ignoreGrace ? TimeSpan.Zero : TimeSpan.FromSeconds(limits.GraceSeconds);
        var idle = TimeSpan.FromMinutes(limits.IdleMinutes);

        var loaded = _store.Profiles.SelectMany(p => p.Tabs).Where(t => t.View != null).ToList();
        var excess = loaded.Count - limits.MaxLoadedTabs;

        foreach (var tab in loaded.Where(t => CanUnload(t, now, grace)).OrderBy(t => t.LastShown))
        {
            // Oldest first: once one is neither idle nor needed to get under the limit, none after it is.
            if (excess <= 0 && now - tab.LastShown <= idle) break;
            UnloadTab(tab);
            excess--;
        }
        UpdateStats();
    }

    bool CanUnload(BrowserTab tab, DateTime now, TimeSpan grace)
    {
        if (tab == ActiveTab || tab.IsLoading || now < tab.ProtectedUntil) return false;
        if (tab.View?.CoreWebView2 is not { } web) return false;   // still starting up
        return !web.IsDocumentPlayingAudio && now - tab.LastShown > grace;
    }

    void UnloadTab(BrowserTab tab)
    {
        var view = tab.View!;
        var url = CurrentUrl(tab);
        tab.View = null;
        tab.PendingUrl = url == "about:blank" ? null : url;
        tab.IsLoading = false;
        var owner = _store.Profiles.FirstOrDefault(pr => pr.Tabs.Contains(tab));
        if (owner != null) RetireView(owner, view);
        else { WebHost.Children.Remove(view); view.Dispose(); }
        ReleaseUnusedProxyEngines();
    }

    // Hidden tabs ask WebView2 to trim their caches; not supported by very old runtimes, so ignore failures.
    void SetMemoryLevel(BrowserTab tab, bool visible)
    {
        try
        {
            if (tab.View?.CoreWebView2 is { } web)
                web.MemoryUsageTargetLevel = visible || !_settings.Limits.LowMemoryHiddenTabs
                    ? CoreWebView2MemoryUsageTargetLevel.Normal
                    : CoreWebView2MemoryUsageTargetLevel.Low;
        }
        catch (Exception)
        {
        }
    }

    // Sidebar diagnostics: tabs loaded vs the limit, and the memory of this app plus its browser processes.
    void UpdateStats()
    {
        var tabs = _store.Profiles.SelectMany(p => p.Tabs).ToList();
        var loaded = tabs.Count(t => t.View != null);
        StatsText.Text = $"{ModeName()} | {loaded}/{_settings.Limits.MaxLoadedTabs} tabs loaded | {AppMemoryMb()} MB";
        StatsText.ToolTip = $"{loaded} tabs loaded of {_settings.Limits.MaxLoadedTabs} allowed in {ModeName()} mode, " +
                            $"{tabs.Count - loaded} unloaded. {AppMemoryMb()} MB used by this app and its browser processes.";
    }

    string ModeName() => _settings.Mode switch
    {
        AppSettings.LowMemory => "Low RAM",
        AppSettings.Performance => "Fast",
        AppSettings.Custom => "Custom",
        _ => "Balanced",
    };

    // ---- modes ----------------------------------------------------------------------------

    // ---- profile panel: hide it for more room, switch profiles from the toolbar chip -------

    void ApplySidebar()
    {
        var hidden = _settings.SidebarCollapsed;
        SidebarColumn.Width = hidden ? new GridLength(0) : new GridLength(240);
        Sidebar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        ProfileChip.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
    }

    void ToggleSidebar()
    {
        _settings.SidebarCollapsed = !_settings.SidebarCollapsed;
        _settings.SidebarSetByUser = true;
        _settings.Save();
        SwitcherPopup.IsOpen = false;
        ApplySidebar();
    }

    void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    void ProfileChip_Click(object sender, RoutedEventArgs e) => OpenSwitcher();

    // Ctrl+K: focus the search box, or, with the panel hidden, open the quick switcher under the chip.
    void OpenSwitcher()
    {
        if (!_settings.SidebarCollapsed)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }
        SwitcherSearch.Text = "";
        FillSwitcher();
        SwitcherPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => SwitcherSearch.Focus(), DispatcherPriority.Input);
    }

    void FillSwitcher()
    {
        var q = SwitcherSearch.Text.Trim();
        var list = _store.Profiles
            .Where(p => !p.IsDeleted && (q == "" || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                 || p.Group.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.GroupName).ThenBy(p => p.Name)
            .Take(60).ToList();
        SwitcherList.ItemsSource = list;
        SwitcherList.SelectedIndex = list.Count > 0 ? 0 : -1;
    }

    void Switcher_TextChanged(object sender, TextChangedEventArgs e) => FillSwitcher();

    void Switcher_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                SwitcherList.SelectedIndex = Math.Min(SwitcherList.SelectedIndex + 1, SwitcherList.Items.Count - 1);
                SwitcherList.ScrollIntoView(SwitcherList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                SwitcherList.SelectedIndex = Math.Max(SwitcherList.SelectedIndex - 1, 0);
                SwitcherList.ScrollIntoView(SwitcherList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                if (SwitcherList.SelectedItem is Profile p) PickProfile(p);
                e.Handled = true;
                break;
            case Key.Escape:
                SwitcherPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    void SwitcherList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(SwitcherList, source) is ListBoxItem { DataContext: Profile p })
        {
            PickProfile(p);
            e.Handled = true;
        }
    }

    void PickProfile(Profile p)
    {
        SwitcherPopup.IsOpen = false;
        _showTrash = false;
        SearchBox.Clear();   // so the hidden list is not filtered
        RefreshView();
        ProfileList.SelectedItem = p;   // works while the panel is hidden
    }

    void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode } || mode == _settings.Mode) return;
        _settings.Mode = mode;
        _settings.Save();
        ApplyMode();
    }

    // Takes effect immediately, no restart: tab limits are enforced now (the just-used protection is
    // skipped, since you asked for it) and hidden tabs switch memory level.
    void ApplyMode()
    {
        foreach (var t in _store.Profiles.SelectMany(p => p.Tabs))
            SetMemoryLevel(t, visible: t == ActiveTab);
        HighlightMode();
        TrimMemory(ignoreGrace: true);
    }

    void HighlightMode()
    {
        foreach (var b in new[] { ModeLowBtn, ModeBalancedBtn, ModePerformanceBtn })
        {
            var on = (string)b.Tag == _settings.Mode;
            b.Background = on ? (Brush)FindResource("RaisedBrush") : Brushes.Transparent;
            b.Foreground = (Brush)FindResource(on ? "TextBrush" : "DimBrush");
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    long AppMemoryMb()
    {
        long bytes = 0;
        try { bytes += Process.GetCurrentProcess().PrivateMemorySize64; } catch (Exception) { }
        try
        {
            foreach (var env in _proxyEnvs.Values.Prepend(_env).OfType<CoreWebView2Environment>())
                foreach (var info in env.GetProcessInfos())
                    try { bytes += Process.GetProcessById(info.ProcessId).PrivateMemorySize64; } catch (Exception) { }
        }
        catch (Exception) { }
        return bytes / (1024 * 1024);
    }

    // Mouse wheel scrolls the tab strip sideways (it can hold thousands of tabs).
    void TabList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScroll(TabList) is not { } scroll) return;
        if (e.Delta > 0) scroll.LineLeft(); else scroll.LineRight();
        e.Handled = true;
    }

    static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    // ---- navigation and shortcuts ---------------------------------------------------------

    void Back_Click(object sender, RoutedEventArgs e) { if (ActiveWeb is { CanGoBack: true } w) w.GoBack(); }
    void Forward_Click(object sender, RoutedEventArgs e) { if (ActiveWeb is { CanGoForward: true } w) w.GoForward(); }
    void Reload_Click(object sender, RoutedEventArgs e) => ActiveWeb?.Reload();

    void Address_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;   // Alt+Enter arrives as a "system" key
        if (key != Key.Enter) return;
        e.Handled = true;
        var inNewTab = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;   // Alt+Enter opens a new tab
        if (SuggestPopup.IsOpen && SuggestList.SelectedItem is AddressSuggestion pick)
        {
            OpenSuggestion(pick.Url, inNewTab);
            return;
        }
        SuggestPopup.IsOpen = false;
        if (inNewTab && _current is { } p)
        {
            var url = Normalize(Address.Text);
            _ = NewTabAsync(p, url);
            return;
        }
        ActiveWeb?.Navigate(Normalize(Address.Text));
        ActiveTab?.View?.Focus();   // so the page, not the address box, gets the next key press
    }

    // The address of the page in the active tab, as the address bar shows it when you are not typing.
    string CurrentAddress()
    {
        if (ActiveTab is not { } tab) return "";
        var src = tab.View?.CoreWebView2?.Source ?? tab.PendingUrl;
        return string.IsNullOrEmpty(src) || src == "about:blank" ? "" : src;
    }

    // ---- suggested sites in the address bar -----------------------------------------------

    // Lock for https, warning for plain http, magnifier when the text is a search rather than an address.
    void UpdateSecurityIcon(string text)
    {
        var t = text.Trim();
        var (glyph, color, tip) =
            t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? ("", "#3DBE7A", "Secure connection (HTTPS)")
            : t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? ("", "#F5A442", "Not secure: this page uses HTTP")
            : t.Contains("://") ? ("", "#6B6E78", "Browser page")
            : ("", "#6B6E78", "Type an address or search");
        SecurityIcon.Text = glyph;
        SecurityIcon.Foreground = (Brush)new BrushConverter().ConvertFromString(color)!;
        SecurityIcon.ToolTip = tip;
    }

    void Address_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSecurityIcon(Address.Text);
        // Only while the user is typing, not when a page navigation rewrites the address.
        if (!Address.IsKeyboardFocusWithin) { SuggestPopup.IsOpen = false; return; }
        // The profile's own history only: another client's pages never show up in this client's suggestions.
        var history = _current == null ? new List<HistoryEntry>() : _history.Search(_current.Id, Address.Text, 12);
        var sites = _suggestions.Matching(Address.Text, 6);
        var rows = AddressSuggestions.Build(Address.Text, EffectiveEngineId, history, sites, OtaCatalog.Find(Address.Text, 6));
        SuggestList.ItemsSource = rows;
        SuggestList.SelectedIndex = -1;
        SuggestPopup.IsOpen = rows.Count > 0;
    }

    void Address_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SuggestPopup.IsOpen = false;

    // A click in the address bar selects the whole address (as in other browsers), so typing replaces it; a second
    // click, once it has focus, places the caret as usual.
    void Address_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Address.IsKeyboardFocusWithin) return;
        Address.Focus();
        Address.SelectAll();
        e.Handled = true;
    }

    void Address_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Escape with no list open gives the page's own address back and selects it, undoing a half-typed change.
        if (e.Key == Key.Escape && !SuggestPopup.IsOpen)
        {
            Address.Text = CurrentAddress();
            Address.SelectAll();
            e.Handled = true;
            return;
        }
        if (!SuggestPopup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                SuggestList.SelectedIndex = Math.Min(SuggestList.SelectedIndex + 1, SuggestList.Items.Count - 1);
                e.Handled = true;
                break;
            case Key.Up:
                SuggestList.SelectedIndex = Math.Max(SuggestList.SelectedIndex - 1, -1);
                e.Handled = true;
                break;
            case Key.Escape:
                SuggestPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    // Handled on mouse-down so the address box does not lose focus (and close the list) first.
    void SuggestList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(SuggestList, source) is ListBoxItem { DataContext: AddressSuggestion pick })
        {
            OpenSuggestion(pick.Url, (Keyboard.Modifiers & ModifierKeys.Control) != 0);   // Ctrl+click: new tab
            e.Handled = true;
        }
    }

    void OpenSuggestion(string url, bool inNewTab = false)
    {
        SuggestPopup.IsOpen = false;
        if (inNewTab && _current is { } p)
        {
            _ = NewTabAsync(p, url);   // the address box then shows the page being replaced, unchanged until the new tab selects
            return;
        }
        Address.Text = url;
        SuggestPopup.IsOpen = false;   // after the text change, which would reopen it
        ActiveWeb?.Navigate(url);
    }

    string Normalize(string text) => SearchEngines.ToUrl(text, EffectiveEngineId);

    // The search button next to the address bar: pick the engine used for text that is not an address.
    // The engine for text typed in the address bar: the open profile's own choice, else the app default.
    string EffectiveEngineId => SearchEngines.Effective(_current?.SearchEngine, _settings.SearchEngine);

    // The magnifier button: choose the engine for this profile, or the app-wide default.
    void SearchEngine_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SearchBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        static MenuItem Choice(string header, bool selected, Action pick)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = selected };
            item.Click += (_, _) => pick();
            return item;
        }

        if (_current is { } profile)
        {
            menu.Items.Add(new MenuItem { Header = $"For \"{profile.Name}\"", IsEnabled = false, FontWeight = FontWeights.SemiBold });
            menu.Items.Add(Choice($"App default ({SearchEngines.Get(_settings.SearchEngine).Name})", !SearchEngines.IsKnown(profile.SearchEngine),
                () => SetProfileEngine(profile, "")));
            foreach (var engine in SearchEngines.All)
            {
                var chosen = engine;
                menu.Items.Add(Choice($"{engine.Name}    ({engine.Keywords[0]} ...)", profile.SearchEngine == engine.Id,
                    () => SetProfileEngine(profile, chosen.Id)));
            }
            menu.Items.Add(new Separator());
        }
        var appDefault = new MenuItem { Header = "App default for all profiles" };
        foreach (var engine in SearchEngines.All)
        {
            var chosen = engine;
            appDefault.Items.Add(Choice(engine.Name, engine.Id == _settings.SearchEngine, () =>
            {
                _settings.SearchEngine = chosen.Id;
                _settings.SearchEngineSetByUser = true;
                _settings.Save();
                UpdateSearchButton();
            }));
        }
        menu.Items.Add(appDefault);
        menu.IsOpen = true;
    }

    void SetProfileEngine(Profile profile, string engineId)
    {
        profile.SearchEngine = engineId;
        _store.Save();
        UpdateSearchButton();
    }

    void UpdateSearchButton()
    {
        var engine = SearchEngines.Get(EffectiveEngineId);
        var own = _current != null && SearchEngines.IsKnown(_current.SearchEngine);
        SearchBtn.ToolTip = $"Search engine: {engine.Name}{(own ? " (set for this profile)" : " (app default)")}. " +
                            "Click to change it. Type a keyword first to use another once, e.g. \"ddg hotel rates\".";
        Address.ToolTip = $"Type an address, or search with {engine.Name}";
    }

    // Keys pressed inside a page are reported to the app, because the WPF control hides them (see TabShortcuts for the
    // list). F5, Ctrl+R and Alt+Left/Right already work inside a page, so they are not forwarded. Top-level page only.
    const string ShortcutScript =
        "window.addEventListener('keydown', e => {" +
        " if (window !== window.top) return;" +
        " const k = e.key, lk = k.toLowerCase(); let m = null;" +
        " if (e.ctrlKey && !e.altKey) {" +
        "  if (!e.shiftKey && lk.length === 1 && 'twlkhb'.includes(lk)) m = lk;" +
        "  else if (k === 'Tab') m = e.shiftKey ? 'p' : 'n';" +
        "  else if (!e.shiftKey && k === 'PageDown') m = 'n';" +
        "  else if (!e.shiftKey && k === 'PageUp') m = 'p';" +
        "  else if (!e.shiftKey && k === 'F4') m = 'w';" +
        "  else if (!e.shiftKey && k.length === 1 && k >= '1' && k <= '9') m = k;" +
        "  else if (e.shiftKey && lk === 't') m = 'z';" +
        " } else if (!e.ctrlKey && !e.shiftKey && !e.altKey && k === 'F6') m = 'l';" +
        " else if (e.altKey && !e.ctrlKey && !e.shiftKey && lk === 'd') m = 'l';" +
        " if (m) { e.preventDefault(); window.chrome.webview.postMessage(m); }" +
        "}, true);" +
        // Ctrl-click or middle-click on a link: tell the app, so the tab it opens stays in the background.
        "const bgLink = e => {" +
        " if (window !== window.top) return;" +
        " if (!(e.button === 1 || (e.button === 0 && (e.ctrlKey || e.metaKey)))) return;" +
        " if (e.target && e.target.closest && e.target.closest('a[href]')) window.chrome.webview.postMessage('o');" +
        "};" +
        "window.addEventListener('click', bgLink, true); window.addEventListener('auxclick', bgLink, true);";

    DateTime _openInBackgroundUntil = DateTime.MinValue;

    // Right-click menu: "Open link in new tab" on a link, and a search for the selected text in a new tab.
    void AddContextMenuItems(Profile p, CoreWebView2 web, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        try
        {
            var target = args.ContextMenuTarget;
            var items = args.MenuItems;
            var at = 0;
            if (target.HasLinkUri && Uri.TryCreate(target.LinkUri, UriKind.Absolute, out var link) &&
                (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps))
            {
                var item = web.Environment.CreateContextMenuItem("Open link in new tab", null, CoreWebView2ContextMenuItemKind.Command);
                item.CustomItemSelected += (_, _) => Dispatcher.InvokeAsync(() => OpenInNewTabAsync(p, link.AbsoluteUri));
                items.Insert(at++, item);
            }
            if (target.HasSelection && (target.SelectionText ?? "").Trim() is { Length: > 0 } selected)
            {
                var engine = SearchEngines.Get(SearchEngines.Effective(p.SearchEngine, _settings.SearchEngine));
                var shown = selected.Length > 30 ? selected[..30] + "..." : selected;
                var item = web.Environment.CreateContextMenuItem($"Search {engine.Name} for \"{shown}\"", null, CoreWebView2ContextMenuItemKind.Command);
                item.CustomItemSelected += (_, _) => Dispatcher.InvokeAsync(() => OpenInNewTabAsync(p, engine.UrlFor(selected)));
                items.Insert(at++, item);
            }
        }
        catch (Exception ex) { CrashLog.Write("context menu", ex); }
    }

    async Task OpenInNewTabAsync(Profile p, string url)
    {
        if (_current != p) return;
        await NewTabAsync(p, url);
    }

    bool HandleShortcut(char key)
    {
        switch (key)
        {
            case 't': _ = StartNewTabAsync(); return true;
            case 'w': if (ActiveTab is { } t) _ = CloseTabAsync(t); return true;
            case 'l': Address.Focus(); Address.SelectAll(); return true;
            case 'k': OpenSwitcher(); return true;
            case 'b': ToggleSidebar(); return true;
            case 'h': _ = ShowHistoryAsync(_current); return true;
            case 'r': ActiveWeb?.Reload(); return true;
            case '<': if (ActiveWeb is { CanGoBack: true } back) back.GoBack(); return true;
            case '>': if (ActiveWeb is { CanGoForward: true } forward) forward.GoForward(); return true;
            case 'z': _ = ReopenClosedTabAsync(); return true;
            case 'n' or 'p' or (>= '1' and <= '9'): GoToTab(key); return true;
            default: return false;
        }
    }

    void GoToTab(char action)
    {
        if (_current is not { } p || p.Tabs.Count == 0) return;
        var at = TabShortcuts.TabFor(action, p.Tabs.Count, ActiveTab == null ? -1 : p.Tabs.IndexOf(ActiveTab));
        if (at is { } i && p.Tabs[i] != ActiveTab) SelectTab(p.Tabs[i]);
    }

    // ---- reopen closed tab (Ctrl+Shift+T) --------------------------------------------------

    // The last tabs closed in each profile, newest last. Kept per profile, so Ctrl+Shift+T never brings back a page
    // from another client's profile, and only in memory (not saved).
    readonly Dictionary<string, List<(string Url, string Title)>> _closedTabs = new();
    const int MaxClosedTabs = 10;

    void RememberClosedTab(Profile p, BrowserTab tab)
    {
        var url = CurrentUrl(tab);
        if (string.IsNullOrEmpty(url) || url == "about:blank") return;
        if (!_closedTabs.TryGetValue(p.Id, out var list)) _closedTabs[p.Id] = list = new();
        list.Add((url, tab.Title));
        if (list.Count > MaxClosedTabs) list.RemoveAt(0);
    }

    async Task ReopenClosedTabAsync()
    {
        if (_current is not { } p || !_closedTabs.TryGetValue(p.Id, out var list) || list.Count == 0) return;
        var (url, _) = list[^1];
        list.RemoveAt(list.Count - 1);
        await NewTabAsync(p, url);
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && ProfileList.SelectedItem is Profile selected && e.OriginalSource is not TextBox)
        {
            Rename(selected);
            e.Handled = true;
            return;
        }
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key;   // Alt combinations arrive as a "system" key
        if (TabShortcuts.FromKey(pressed, Keyboard.Modifiers) is { } action && HandleShortcut(action)) e.Handled = true;
    }
}
