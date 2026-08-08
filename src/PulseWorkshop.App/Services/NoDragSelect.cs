using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PulseWorkshop.App.Services;

/// <summary>
/// Kills WPF's rubber-band selection in every multi-select ListBox in the app (Unpack's file list,
/// the Workshop lists, the Advanced/Textures entry lists). <see cref="ListBoxItem"/> extends the
/// selection from its <c>OnMouseEnter</c> whenever <em>either</em> the left or the right button is
/// held, so holding a button anywhere and sweeping across rows silently re-selects them - and a
/// held right-click does it too, which is never what a context menu is asking for.
///
/// Suppressed with one class handler rather than a ListBox subclass: the handler runs before
/// UIElement's thunk (which is what invokes the virtual), so marking the event handled means
/// <c>OnMouseEnter</c> never runs and no XAML has to opt in. Only while a button is down, so
/// ordinary hover - highlight, tooltips - is untouched. Click, Ctrl-click and Shift-click still
/// select normally; those go through mouse-down, not mouse-enter.
/// </summary>
public static class NoDragSelect
{
    /// <summary>Hooks the app-wide handler. Call once at startup.</summary>
    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(ListBoxItem), Mouse.MouseEnterEvent,
            new MouseEventHandler(OnMouseEnter));

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
            e.Handled = true;
    }
}
