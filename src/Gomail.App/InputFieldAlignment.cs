using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gomail_App;

public static class InputFieldAlignment
{
    public static readonly DependencyProperty CenterContentProperty = DependencyProperty.RegisterAttached(
        "CenterContent",
        typeof(bool),
        typeof(InputFieldAlignment),
        new PropertyMetadata(false, CenterContentChanged));

    public static bool GetCenterContent(DependencyObject element) =>
        (bool)element.GetValue(CenterContentProperty);

    public static void SetCenterContent(DependencyObject element, bool value) =>
        element.SetValue(CenterContentProperty, value);

    private static void CenterContentChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Control control) return;

        if (args.NewValue is true)
        {
            control.Loaded += Control_Loaded;
            if (control.IsLoaded) Apply(control);
        }
        else
        {
            control.Loaded -= Control_Loaded;
        }
    }

    private static void Control_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Control control) Apply(control);
    }

    private static void Apply(Control control)
    {
        control.ApplyTemplate();
        CenterTemplatePart(control, "ContentElement");
        CenterTemplatePart(control, "PlaceholderTextContentPresenter");
    }

    private static void CenterTemplatePart(DependencyObject root, string name)
    {
        if (FindDescendant(root, name) is not FrameworkElement element) return;
        element.VerticalAlignment = VerticalAlignment.Center;
        if (element is Control contentControl)
        {
            contentControl.VerticalContentAlignment = VerticalAlignment.Center;
        }
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement { Name: var childName } element && childName == name) return element;
            if (FindDescendant(child, name) is { } descendant) return descendant;
        }

        return null;
    }
}
