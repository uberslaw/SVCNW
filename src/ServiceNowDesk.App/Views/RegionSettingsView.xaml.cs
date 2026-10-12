using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class RegionSettingsView : UserControl
{
    public static readonly DependencyProperty ShowIntroProperty = DependencyProperty.Register(
        nameof(ShowIntro),
        typeof(bool),
        typeof(RegionSettingsView),
        new PropertyMetadata(true));

    public RegionSettingsView()
    {
        InitializeComponent();
    }

    public bool ShowIntro
    {
        get => (bool)GetValue(ShowIntroProperty);
        set => SetValue(ShowIntroProperty, value);
    }

    private void AllOffices_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ToggleGroup(sender))
            return;
        e.Handled = true;
    }

    private void AllOffices_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter))
            return;
        if (!ToggleGroup(sender))
            return;
        e.Handled = true;
    }

    private static bool ToggleGroup(object sender)
    {
        if (sender is not CheckBox { DataContext: RegionOfficeNode node } || node.IsOffice)
            return false;
        node.ToggleFromUser();
        return true;
    }
}
