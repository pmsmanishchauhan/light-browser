using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
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
        HighlightMode();
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
        };
    }

    void UpdateEmptyState()
    {
        EmptyPanel.Visibility = _current == null ? Visibility.Visible : Visibility.Collapsed;
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
        var dlg = new ProfileDialog(null, _suggestions.Sites, GroupSitesFor(null), ExistingNames(null)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var p = new Profile
        {
            Name = dlg.ProfileName, Color = dlg.ColorHex, HomeUrls = dlg.HomeUrls,
            Group = CanonicalGroup(dlg.GroupName), Notes = dlg.Notes,
        };
        _store.Profiles.Add(p);
        _store.Save();

        _showTrash = false;
        SearchBox.Clear();      // so the new profile is not filtered out
        RefreshView();
        ProfileList.SelectedItem = p;
    }

    void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileOf(sender) is not { } p) return;
        var dlg = new ProfileDialog(p, _suggestions.Sites, GroupSitesFor(p), ExistingNames(p)) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        p.Name = dlg.ProfileName;
        p.Color = dlg.ColorHex;
        p.HomeUrls = dlg.HomeUrls;
        p.Group = CanonicalGroup(dlg.GroupName);
        p.Notes = dlg.Notes;
        _store.Save();
        if (p == _current) ApplyProfileChrome();
        RefreshView();
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
        var view = new WebView2();
        tab.View = view;
        tab.PendingUrl = null;
        WebHost.Children.Add(view);
        if (_current == p) SelectTab(tab);   // must be visible before WebView2 can create its window

        var options = _env!.CreateCoreWebView2ControllerOptions();
        options.ProfileName = p.WebViewProfileName;
        options.IsInPrivateModeEnabled = false;
        await view.EnsureCoreWebView2Async(_env, options);
        var web = view.CoreWebView2;
        web.Settings.IsPasswordAutosaveEnabled = true;   // "Save password?" prompt, stored per profile
        web.Settings.IsGeneralAutofillEnabled = true;
        SetMemoryLevel(tab, visible: ActiveTab == tab);

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
            if (_current != p) SelectProfileQuietly(p);
            if (_current != p) await ShowProfileAsync(p, openPages: false);
            var popup = await NewTabAsync(p, null);
            args.NewWindow = popup.View!.CoreWebView2;
            deferral.Complete();
        };
        // window.close() from a popup (e.g. a login window finishing) closes its tab.
        web.WindowCloseRequested += (_, _) => Dispatcher.InvokeAsync(() => CloseTabAsync(tab));

        // The WPF control hides the controller's key events, so the page reports Ctrl+T/W/L/K itself.
        await web.AddScriptToExecuteOnDocumentCreatedAsync(ShortcutScript);
        web.WebMessageReceived += (_, args) =>
        {
            // Only the page itself may trigger a shortcut, not an embedded frame from another site.
            if (!SameOrigin(args.Source, web.Source)) return;
            var msg = args.TryGetWebMessageAsString();
            if (msg is { Length: 1 }) HandleShortcut(msg[0]);
        };

        if (url != null && url != "about:blank") web.Navigate(url);
        TrimMemory();
    }

    Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var options = string.IsNullOrWhiteSpace(_settings.BrowserArguments)
            ? null
            : new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = _settings.BrowserArguments };
        return CoreWebView2Environment.CreateAsync(null, ProfileStore.DataDir, options);
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
        if (tab.View != null)
        {
            WebHost.Children.Remove(tab.View);
            tab.View.Dispose();
        }
        if (p.Active == tab) p.Active = null;
    }

    async Task CloseTabAsync(BrowserTab tab)
    {
        var p = _store.Profiles.FirstOrDefault(x => x.Tabs.Contains(tab));
        if (p == null) return;
        var index = p.Tabs.IndexOf(tab);
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
        if (tab == ActiveTab || tab.IsLoading) return false;
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
        WebHost.Children.Remove(view);
        view.Dispose();
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
        StatsText.Text = $"{ModeName()}: {loaded} of {_settings.Limits.MaxLoadedTabs} tabs loaded, {tabs.Count - loaded} unloaded\n{AppMemoryMb()} MB in use";
    }

    string ModeName() => _settings.Mode switch
    {
        AppSettings.LowMemory => "Low RAM",
        AppSettings.Performance => "Fast",
        AppSettings.Custom => "Custom",
        _ => "Balanced",
    };

    // ---- modes ----------------------------------------------------------------------------

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
            b.Background = (string)b.Tag == _settings.Mode ? new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
    }

    long AppMemoryMb()
    {
        long bytes = 0;
        try { bytes += Process.GetCurrentProcess().PrivateMemorySize64; } catch (Exception) { }
        try
        {
            if (_env != null)
                foreach (var info in _env.GetProcessInfos())
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
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (SuggestPopup.IsOpen && SuggestList.SelectedItem is string pick)
        {
            OpenSuggestion(pick);
            return;
        }
        SuggestPopup.IsOpen = false;
        ActiveWeb?.Navigate(Normalize(Address.Text));
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
        var hits = _suggestions.Matching(Address.Text, 6);
        SuggestList.ItemsSource = hits;
        SuggestList.SelectedIndex = -1;
        SuggestPopup.IsOpen = hits.Count > 0;
    }

    void Address_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SuggestPopup.IsOpen = false;

    void Address_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        if (ItemsControl.ContainerFromElement(SuggestList, source) is ListBoxItem { DataContext: string url })
        {
            OpenSuggestion(url);
            e.Handled = true;
        }
    }

    void OpenSuggestion(string url)
    {
        Address.Text = url;
        SuggestPopup.IsOpen = false;   // after the text change, which would reopen it
        ActiveWeb?.Navigate(url);
    }

    static string Normalize(string text)
    {
        text = text.Trim();
        if (text.Contains("://") || text.StartsWith("about:")) return text;
        if (!text.Contains(' ') && (text.Contains('.') || text.StartsWith("localhost"))) return "https://" + text;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
    }

    // Ctrl+T new tab, Ctrl+W close tab, Ctrl+L address bar, Ctrl+K profile search. Top-level page only.
    const string ShortcutScript =
        "window.addEventListener('keydown', e => {" +
        " if (window !== window.top) return;" +
        " if (!e.ctrlKey || e.altKey || e.shiftKey) return;" +
        " const k = e.key.toLowerCase();" +
        " if (k === 't' || k === 'w' || k === 'l' || k === 'k' || k === 'h') { e.preventDefault(); window.chrome.webview.postMessage(k); }" +
        "}, true);";

    bool HandleShortcut(char key)
    {
        switch (key)
        {
            case 't': _ = StartNewTabAsync(); return true;
            case 'w': if (ActiveTab is { } t) _ = CloseTabAsync(t); return true;
            case 'l': Address.Focus(); Address.SelectAll(); return true;
            case 'k': SearchBox.Focus(); SearchBox.SelectAll(); return true;
            case 'h': _ = ShowHistoryAsync(_current); return true;
            default: return false;
        }
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && ProfileList.SelectedItem is Profile selected && e.OriginalSource is not TextBox)
        {
            Rename(selected);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var key = e.Key switch { Key.T => 't', Key.W => 'w', Key.L => 'l', Key.K => 'k', Key.H => 'h', _ => '\0' };
        if (key != '\0' && HandleShortcut(key)) e.Handled = true;
    }
}
