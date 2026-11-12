using System.Windows.Input;

namespace LightBrowser;

// Keyboard shortcuts, as in other browsers. FromKey turns a key press into a one-character action code that
// MainWindow.HandleShortcut runs; the page script (ShortcutScript) sends the same codes for keys pressed inside a page.
//
//   t new tab          w close tab          l address bar        k profile search      h history     b profile panel
//   n next tab         p previous tab       1-8 tab 1-8          9 last tab            z reopen closed tab
//   r reload           < back               > forward
public static class TabShortcuts
{
    public static char? FromKey(Key key, ModifierKeys mods)
    {
        var ctrl = (mods & ModifierKeys.Control) != 0;
        var shift = (mods & ModifierKeys.Shift) != 0;
        var alt = (mods & ModifierKeys.Alt) != 0;

        if (ctrl && !alt)
        {
            if (shift)
                return key switch { Key.Tab => 'p', Key.T => 'z', Key.R => 'r', _ => null };
            return key switch
            {
                Key.T => 't', Key.W => 'w', Key.F4 => 'w', Key.L => 'l', Key.K => 'k', Key.H => 'h', Key.B => 'b', Key.R => 'r',
                Key.Tab => 'n', Key.PageDown => 'n', Key.PageUp => 'p',
                >= Key.D1 and <= Key.D9 => (char)('1' + (key - Key.D1)),
                >= Key.NumPad1 and <= Key.NumPad9 => (char)('1' + (key - Key.NumPad1)),
                _ => null,
            };
        }
        if (!ctrl && !shift && alt)
            return key switch { Key.Left => '<', Key.Right => '>', Key.D => 'l', _ => null };
        if (mods == ModifierKeys.None)
            return key switch { Key.F5 => 'r', Key.F6 => 'l', _ => null };
        return null;
    }

    // Which tab a "go to tab" action selects, given how many tabs there are and which one is shown. Null when there is
    // no such tab. "n" and "p" wrap around. Positions are 0-based.
    public static int? TabFor(char action, int count, int current)
    {
        if (count <= 0) return null;
        switch (action)
        {
            case 'n': return (current + 1) % count;
            case 'p': return (current - 1 + count) % count;
            case '9': return count - 1;
            case >= '1' and <= '8':
                var index = action - '1';
                return index < count ? index : null;
            default: return null;
        }
    }
}
