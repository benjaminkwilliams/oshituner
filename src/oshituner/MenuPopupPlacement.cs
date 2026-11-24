using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace oshituner;

public static class MenuPopupPlacement
{
    public static readonly DependencyProperty AlignProperty = DependencyProperty.RegisterAttached(
        "Align", typeof(bool), typeof(MenuPopupPlacement), new PropertyMetadata(false, AlignChanged));

    public static bool GetAlign(DependencyObject element) => (bool)element.GetValue(AlignProperty);

    public static void SetAlign(DependencyObject element, bool value) => element.SetValue(AlignProperty, value);

    private static void AlignChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not MenuItem item) return;
        if ((bool)args.NewValue)
        {
            item.Loaded += MenuItemLoaded;
            if (item.IsLoaded) AlignPopup(item);
        }
        else item.Loaded -= MenuItemLoaded;
    }

    private static void MenuItemLoaded(object sender, RoutedEventArgs args) => AlignPopup((MenuItem)sender);

    private static void AlignPopup(MenuItem item)
    {
        if (!item.HasItems && item.Role is not (MenuItemRole.TopLevelHeader or MenuItemRole.SubmenuHeader)) return;
        item.ApplyTemplate();
        if (item.Template.FindName("PART_Popup", item) is not Popup popup) return;

        popup.PlacementTarget = item;
        popup.HorizontalOffset = 0;
        popup.VerticalOffset = 0;
        popup.CustomPopupPlacementCallback = (size, target, _) => item.Role == MenuItemRole.TopLevelHeader
            ? [new(new Point(0, target.Height), PopupPrimaryAxis.Horizontal),
               new(new Point(0, -size.Height), PopupPrimaryAxis.Horizontal)]
            : [new(new Point(target.Width, 0), PopupPrimaryAxis.Vertical),
               new(new Point(-size.Width, 0), PopupPrimaryAxis.Vertical)];
        popup.Placement = PlacementMode.Custom;
    }
}
