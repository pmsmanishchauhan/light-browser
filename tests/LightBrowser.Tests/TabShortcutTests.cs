using System.Windows.Input;
using LightBrowser;
using Xunit;

namespace LightBrowser.Tests;

public class TabShortcutTests
{
    const ModifierKeys Ctrl = ModifierKeys.Control, Shift = ModifierKeys.Shift, Alt = ModifierKeys.Alt;

    [Theory]
    [InlineData(Key.T, Ctrl, 't')]
    [InlineData(Key.W, Ctrl, 'w')]
    [InlineData(Key.F4, Ctrl, 'w')]
    [InlineData(Key.L, Ctrl, 'l')]
    [InlineData(Key.K, Ctrl, 'k')]
    [InlineData(Key.H, Ctrl, 'h')]
    [InlineData(Key.B, Ctrl, 'b')]
    [InlineData(Key.R, Ctrl, 'r')]
    [InlineData(Key.R, Ctrl | Shift, 'r')]
    [InlineData(Key.Tab, Ctrl, 'n')]
    [InlineData(Key.PageDown, Ctrl, 'n')]
    [InlineData(Key.Tab, Ctrl | Shift, 'p')]
    [InlineData(Key.PageUp, Ctrl, 'p')]
    [InlineData(Key.T, Ctrl | Shift, 'z')]
    [InlineData(Key.D1, Ctrl, '1')]
    [InlineData(Key.D8, Ctrl, '8')]
    [InlineData(Key.D9, Ctrl, '9')]
    [InlineData(Key.NumPad2, Ctrl, '2')]
    [InlineData(Key.F5, ModifierKeys.None, 'r')]
    [InlineData(Key.F6, ModifierKeys.None, 'l')]
    [InlineData(Key.Left, Alt, '<')]
    [InlineData(Key.Right, Alt, '>')]
    [InlineData(Key.D, Alt, 'l')]
    public void A_shortcut_key_means_the_expected_action(Key key, ModifierKeys mods, char action) =>
        Assert.Equal(action, TabShortcuts.FromKey(key, mods));

    [Theory]
    [InlineData(Key.T, ModifierKeys.None)]          // plain typing is never a shortcut
    [InlineData(Key.Tab, ModifierKeys.None)]
    [InlineData(Key.D1, ModifierKeys.None)]
    [InlineData(Key.T, Ctrl | Alt)]                 // Ctrl+Alt is AltGr on many keyboards: leave it to typing
    [InlineData(Key.W, Ctrl | Shift)]
    [InlineData(Key.D1, Ctrl | Shift)]
    [InlineData(Key.Left, ModifierKeys.None)]
    [InlineData(Key.Left, Alt | Shift)]
    [InlineData(Key.F5, Ctrl)]
    [InlineData(Key.Z, Ctrl)]
    public void Other_key_presses_are_left_alone(Key key, ModifierKeys mods) => Assert.Null(TabShortcuts.FromKey(key, mods));

    [Theory]
    [InlineData('n', 4, 0, 1)]
    [InlineData('n', 4, 3, 0)]      // wraps to the first tab
    [InlineData('p', 4, 0, 3)]      // wraps to the last tab
    [InlineData('p', 4, 2, 1)]
    [InlineData('1', 4, 3, 0)]
    [InlineData('4', 4, 0, 3)]
    [InlineData('9', 4, 0, 3)]      // Ctrl+9 is always the last tab
    [InlineData('9', 12, 0, 11)]
    [InlineData('8', 12, 0, 7)]
    [InlineData('n', 1, 0, 0)]
    public void The_target_tab_is_worked_out_from_the_count_and_the_current_tab(char action, int count, int current, int expected) =>
        Assert.Equal(expected, TabShortcuts.TabFor(action, count, current));

    [Theory]
    [InlineData('5', 3)]            // there is no fifth tab
    [InlineData('n', 0)]
    [InlineData('t', 3)]
    public void No_tab_when_there_is_none_to_go_to(char action, int count) => Assert.Null(TabShortcuts.TabFor(action, count, 0));
}
