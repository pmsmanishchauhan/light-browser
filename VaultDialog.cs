using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LightBrowser;

// One profile's password list: search, add, edit, delete, copy, and fill the page on screen.
public class VaultDialog : Window
{
    static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x8D, 0x96));
    static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24));
    static readonly Brush Border = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));

    readonly string _profileId;
    readonly string? _pageUrl;
    readonly List<VaultEntry> _entries;
    readonly TextBox _search = new();
    readonly ListBox _list = new();
    readonly TextBlock _status = new();

    public VaultDialog(string profileName, string profileId, string? pageUrl, bool canFill)
    {
        _profileId = profileId;
        _pageUrl = pageUrl;
        _entries = PasswordVault.Load(profileId);
        Title = $"Passwords - {profileName}";
        Width = 560;
        Height = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x25, 0x26, 0x2B));
        Foreground = Fg;
        Ui.DarkTitleBar(this);

        var info = new TextBlock
        {
            Text = "Only this profile can open this list. It is stored on this PC, encrypted for your Windows account.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        _search.Background = Field;
        _search.Foreground = Fg;
        _search.CaretBrush = Fg;
        _search.BorderBrush = Border;
        _search.Padding = new Thickness(8, 6, 8, 6);
        _search.TextChanged += (_, _) => Reload();

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Margin = new Thickness(0, 8, 0, 0);
        _list.ItemContainerStyle = (Style)Application.Current.FindResource("Item");
        _list.ItemTemplate = BuildRowTemplate();
        _list.MouseDoubleClick += (_, _) => { if (canFill) FillSelected(); else EditSelected(); };

        Button Make(string text, double width, Action onClick, bool enabled = true)
        {
            var b = new Button
            {
                Content = text, Width = width, Padding = new Thickness(0, 6, 0, 6), Foreground = Fg, Margin = new Thickness(0, 0, 6, 6),
                Style = (Style)Application.Current.FindResource("Flat"), IsEnabled = enabled,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)),
            };
            b.Click += (_, _) => onClick();
            return b;
        }
        var fill = Make("Fill page", 90, FillSelected, canFill);
        fill.Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x8E, 0xF7));
        fill.ToolTip = canFill ? "Fills the login form of the tab on screen" : "Open a page with a login form first";
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(fill);
        buttons.Children.Add(Make("Copy password", 110, () => Copy(e => e.Password, "Password")));
        buttons.Children.Add(Make("Copy username", 110, () => Copy(e => e.Username, "Username")));
        buttons.Children.Add(Make("Add...", 70, Add));
        buttons.Children.Add(Make("Edit...", 70, EditSelected));
        buttons.Children.Add(Make("Delete", 70, DeleteSelected));
        var close = Make("Close", 70, () => DialogResult = false);
        close.IsCancel = true;
        buttons.Children.Add(close);

        _status.Foreground = Dim;
        _status.FontSize = 11.5;

        var top = new StackPanel();
        top.Children.Add(info);
        top.Children.Add(_search);
        var bottom = new StackPanel();
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        Reload();
        Loaded += (_, _) => _search.Focus();
    }

    // Set when the user chose "Fill page"; the caller runs it on the tab on screen.
    public VaultEntry? FillEntry { get; private set; }

    void Reload()
    {
        var q = _search.Text.Trim();
        var matching = PasswordVault.ForHost(_entries, _pageUrl).Select(e => e.Id).ToHashSet();
        var rows = _entries
            .Where(e => q == "" || e.Site.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || e.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || e.Notes.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => matching.Contains(e.Id))     // entries for the page on screen first
            .ThenBy(e => e.Host).ThenBy(e => e.Username)
            .ToList();
        var selected = (_list.SelectedItem as VaultEntry)?.Id;
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(e => e.Id == selected) ?? rows.FirstOrDefault(e => matching.Contains(e.Id));
        _status.Text = _entries.Count == 0 ? "No passwords yet. Click Add..." : $"{rows.Count} of {_entries.Count} shown";
    }

    VaultEntry? Selected => _list.SelectedItem as VaultEntry;

    void Persist()
    {
        if (!PasswordVault.Save(_profileId, _entries))
            MessageBox.Show(this, "The password list could not be saved to disk.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    void Add()
    {
        var entry = EntryDialog.Ask(this, null, PasswordVault.HostOf(_pageUrl));
        if (entry == null) return;
        _entries.Add(entry);
        Persist();
        Reload();
        _list.SelectedItem = entry;
    }

    void EditSelected()
    {
        if (Selected is not { } current) return;
        var edited = EntryDialog.Ask(this, current, "");
        if (edited == null) return;
        current.Site = edited.Site;
        current.Username = edited.Username;
        current.Password = edited.Password;
        current.Notes = edited.Notes;
        current.Updated = DateTime.UtcNow;
        Persist();
        Reload();
    }

    void DeleteSelected()
    {
        if (Selected is not { } current) return;
        var ok = MessageBox.Show(this, $"Delete the saved password for {current.Display}?", Title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;
        _entries.Remove(current);
        Persist();
        Reload();
    }

    void FillSelected()
    {
        if (Selected is not { } current) return;
        FillEntry = current;
        DialogResult = true;
    }

    // Copies a value and clears the clipboard 30 seconds later if it still holds that value.
    void Copy(Func<VaultEntry, string> pick, string what)
    {
        if (Selected is not { } current) return;
        var value = pick(current);
        try
        {
            Clipboard.SetText(value);
            _status.Text = $"{what} copied. The clipboard is cleared in 30 seconds.";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try { if (Clipboard.ContainsText() && Clipboard.GetText() == value) Clipboard.Clear(); }
                catch (Exception) { }
            };
            timer.Start();
        }
        catch (Exception)
        {
            _status.Text = "Could not use the clipboard right now.";
        }
    }

    static DataTemplate BuildRowTemplate()
    {
        var template = new DataTemplate();
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Host"));
        title.SetValue(TextBlock.ForegroundProperty, Fg);
        var user = new FrameworkElementFactory(typeof(TextBlock));
        user.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Username"));
        user.SetValue(TextBlock.ForegroundProperty, Dim);
        user.SetValue(TextBlock.FontSizeProperty, 11.5);
        stack.AppendChild(title);
        stack.AppendChild(user);
        template.VisualTree = stack;
        return template;
    }
}

// Add or edit one saved login. The password can be shown, and a strong one can be generated.
public class EntryDialog : Window
{
    static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    static readonly Brush Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x24));
    static readonly Brush FieldBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42));

    readonly TextBox _site = NewBox();
    readonly TextBox _user = NewBox();
    readonly PasswordBox _hidden = new();
    readonly TextBox _shown = NewBox();
    readonly TextBox _notes = NewBox();
    readonly CheckBox _show = new() { Content = "Show", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

    EntryDialog(VaultEntry? existing, string defaultSite)
    {
        Title = existing == null ? "Add password" : "Edit password";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x25, 0x26, 0x2B));
        Foreground = Fg;
        Ui.DarkTitleBar(this);

        _site.Text = existing?.Site ?? defaultSite;
        _user.Text = existing?.Username ?? "";
        _hidden.Password = existing?.Password ?? "";
        _shown.Text = _hidden.Password;
        _notes.Text = existing?.Notes ?? "";
        _notes.AcceptsReturn = true;
        _notes.Height = 54;
        _notes.TextWrapping = TextWrapping.Wrap;
        _hidden.Background = Field;
        _hidden.Foreground = Fg;
        _hidden.BorderBrush = FieldBorder;
        _hidden.CaretBrush = Fg;
        _hidden.Padding = new Thickness(6, 5, 6, 5);
        _hidden.Margin = new Thickness(0, 4, 0, 0);
        _shown.Visibility = Visibility.Collapsed;
        _hidden.PasswordChanged += (_, _) => { if (_hidden.Visibility == Visibility.Visible) _shown.Text = _hidden.Password; };
        _shown.TextChanged += (_, _) => { if (_shown.Visibility == Visibility.Visible) _hidden.Password = _shown.Text; };
        _show.Checked += (_, _) => { _shown.Text = _hidden.Password; _shown.Visibility = Visibility.Visible; _hidden.Visibility = Visibility.Collapsed; };
        _show.Unchecked += (_, _) => { _hidden.Password = _shown.Text; _hidden.Visibility = Visibility.Visible; _shown.Visibility = Visibility.Collapsed; };

        Button Make(string text, double width)
        {
            return new Button
            {
                Content = text, Width = width, Padding = new Thickness(0, 6, 0, 6), Foreground = Fg,
                Style = (Style)Application.Current.FindResource("Flat"),
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x42)),
            };
        }
        var generate = Make("Generate", 80);
        generate.Margin = new Thickness(8, 4, 0, 0);
        generate.Click += (_, _) => { _hidden.Password = PasswordVault.Generate(); _shown.Text = _hidden.Password; _show.IsChecked = true; };

        var passwordRow = new DockPanel();
        DockPanel.SetDock(generate, Dock.Right);
        DockPanel.SetDock(_show, Dock.Right);
        passwordRow.Children.Add(generate);
        passwordRow.Children.Add(_show);
        var passwordFields = new Grid();
        passwordFields.Children.Add(_hidden);
        passwordFields.Children.Add(_shown);
        passwordRow.Children.Add(passwordFields);

        var ok = Make("Save", 80);
        ok.Background = new SolidColorBrush(Color.FromRgb(0x4F, 0x8E, 0xF7));
        ok.IsDefault = true;
        ok.Margin = new Thickness(0, 0, 8, 0);
        ok.Click += (_, _) =>
        {
            if (PasswordVault.HostOf(_site.Text) == "") { _site.Focus(); return; }
            if (CurrentPassword == "") { (_show.IsChecked == true ? (Control)_shown : _hidden).Focus(); return; }
            DialogResult = true;
        };
        var cancel = Make("Cancel", 80);
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Label("Website (e.g. admin.booking.com)", 0));
        panel.Children.Add(_site);
        panel.Children.Add(Label("Username or email", 10));
        panel.Children.Add(_user);
        panel.Children.Add(Label("Password", 10));
        panel.Children.Add(passwordRow);
        panel.Children.Add(Label("Notes (optional)", 10));
        panel.Children.Add(_notes);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { (_site.Text == "" ? _site : _user).Focus(); };
    }

    string CurrentPassword => _show.IsChecked == true ? _shown.Text : _hidden.Password;

    static TextBox NewBox() => new()
    {
        Background = Field, Foreground = Fg, BorderBrush = FieldBorder, CaretBrush = Fg,
        Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 4, 0, 0),
    };

    static TextBlock Label(string text, double top) =>
        new() { Text = text, Foreground = Fg, Margin = new Thickness(0, top, 0, 0) };

    // Returns a new entry with the entered values, or null if cancelled.
    public static VaultEntry? Ask(Window owner, VaultEntry? existing, string defaultSite)
    {
        var dlg = new EntryDialog(existing, defaultSite) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;
        return new VaultEntry
        {
            Site = dlg._site.Text.Trim(), Username = dlg._user.Text.Trim(),
            Password = dlg.CurrentPassword, Notes = dlg._notes.Text.Trim(),
        };
    }
}
