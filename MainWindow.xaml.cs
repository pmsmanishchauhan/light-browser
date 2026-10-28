using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LightBrowser;

public partial class MainWindow : Window
{
    readonly ProfileStore _store = ProfileStore.Load();
    readonly ICollectionView _view;
    CoreWebView2Environment? _env;
    Profile? _current;
    bool _suppressSelection;   // set while code (not the user) changes the selected profile
    bool _showTrash;           // sidebar is listing Trash instead of profiles

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
        RefreshView();

        Closing += (_, _) => SaveSession();
        Loaded += async (_, _) =>
        {
            try
            {
                _env = await CoreWebView2Environment.CreateAsync(null, ProfileStore.DataDir);
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
        };
    }

    void UpdateEmptyState()
    {
        EmptyState.Visibility = _current == null ? Visibility.Visible : Visibility.Collapsed;
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
    }

    // Back/forward enabled state and the loading bar follow the active tab.
    void UpdateChrome()
    {
        BackBtn.IsEnabled = ActiveWeb is { CanGoBack: true };
        ForwardBtn.IsEnabled = ActiveWeb is { CanGoForward: true };
        Loading.Visibility = ActiveTab is { IsLoading: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    // Remember each opened profile's tabs so they come back next time.
    static void CaptureSession(Profile p)
    {
        if (p.Tabs.Count == 0) return;   // not opened this run: keep what was saved before
        p.OpenUrls = p.Tabs
            .Select(t => t.View?.CoreWebView2?.Source ?? t.PendingUrl ?? "")
            .Where(s => s != "" && s != "about:blank")
            .ToList();
    }

    void SaveSession()
    {
        foreach (var p in _store.Profiles) CaptureSession(p);
        _store.Save();
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
                if (t.View != null) t.View.Visibility = Visibility.Collapsed;
    }

    async Task ShowProfileAsync(Profile p, bool openPages)
    {
        _current = p;
        HideAllViews();
        TabList.ItemsSource = p.Tabs;
        ApplyProfileChrome();
        UpdateEmptyState();

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

    static void AddLazyTab(Profile p, string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        p.Tabs.Add(new BrowserTab { PendingUrl = url, Title = host });
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
        var dlg = new ProfileDialog { Owner = this };
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
        var dlg = new ProfileDialog(p) { Owner = this };
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

        // Clearing needs a live WebView2 on that profile. Drop unopened tabs, then use a loaded
        // tab or open a blank one.
        foreach (var lazy in p.Tabs.Where(t => t.View == null).ToList()) p.Tabs.Remove(lazy);
        SelectProfileQuietly(p);
        await ShowProfileAsync(p, openPages: false);
        var live = p.Tabs.FirstOrDefault(t => t.View?.CoreWebView2 != null) ?? await NewTabAsync(p, "about:blank");
        await live.View!.CoreWebView2.Profile.ClearBrowsingDataAsync();

        foreach (var t in p.Tabs.ToList()) DisposeTab(p, t);
        _current = null;
        _store.Profiles.Remove(p);
        _store.Save();
        ShowNothing();
        RefreshView();
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

        web.DocumentTitleChanged += (_, _) =>
            tab.Title = string.IsNullOrWhiteSpace(web.DocumentTitle) ? web.Source : web.DocumentTitle;
        web.SourceChanged += (_, _) =>
        {
            if (ActiveTab == tab) Address.Text = web.Source == "about:blank" ? "" : web.Source;
        };
        web.NavigationStarting += (_, _) =>
        {
            tab.IsLoading = true;
            if (ActiveTab == tab) UpdateChrome();
        };
        web.NavigationCompleted += (_, _) =>
        {
            tab.IsLoading = false;
            if (ActiveTab == tab) UpdateChrome();
        };
        web.HistoryChanged += (_, _) => { if (ActiveTab == tab) UpdateChrome(); };

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
            var msg = args.TryGetWebMessageAsString();
            if (msg is { Length: 1 }) HandleShortcut(msg[0]);
        };

        if (url != null && url != "about:blank") web.Navigate(url);
    }

    void SelectTab(BrowserTab tab)
    {
        if (_current == null) return;
        foreach (var t in _current.Tabs)
            if (t.View != null) t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        _current.Active = tab;
        TabList.SelectedItem = tab;

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

    // ---- navigation and shortcuts ---------------------------------------------------------

    void Back_Click(object sender, RoutedEventArgs e) { if (ActiveWeb is { CanGoBack: true } w) w.GoBack(); }
    void Forward_Click(object sender, RoutedEventArgs e) { if (ActiveWeb is { CanGoForward: true } w) w.GoForward(); }
    void Reload_Click(object sender, RoutedEventArgs e) => ActiveWeb?.Reload();

    void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ActiveWeb?.Navigate(Normalize(Address.Text));
        e.Handled = true;
    }

    static string Normalize(string text)
    {
        text = text.Trim();
        if (text.Contains("://") || text.StartsWith("about:")) return text;
        if (!text.Contains(' ') && (text.Contains('.') || text.StartsWith("localhost"))) return "https://" + text;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
    }

    // Ctrl+T new tab, Ctrl+W close tab, Ctrl+L address bar, Ctrl+K profile search.
    const string ShortcutScript =
        "window.addEventListener('keydown', e => {" +
        " if (!e.ctrlKey || e.altKey || e.shiftKey) return;" +
        " const k = e.key.toLowerCase();" +
        " if (k === 't' || k === 'w' || k === 'l' || k === 'k') { e.preventDefault(); window.chrome.webview.postMessage(k); }" +
        "}, true);";

    bool HandleShortcut(char key)
    {
        switch (key)
        {
            case 't': _ = StartNewTabAsync(); return true;
            case 'w': if (ActiveTab is { } t) _ = CloseTabAsync(t); return true;
            case 'l': Address.Focus(); Address.SelectAll(); return true;
            case 'k': SearchBox.Focus(); SearchBox.SelectAll(); return true;
            default: return false;
        }
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var key = e.Key switch { Key.T => 't', Key.W => 'w', Key.L => 'l', Key.K => 'k', _ => '\0' };
        if (key != '\0' && HandleShortcut(key)) e.Handled = true;
    }
}
