using System.Windows;

namespace ServiceNowDesk.Views;

public partial class WorkEffortCalcLogWindow : Window
{
    public WorkEffortCalcLogWindow(string text)
    {
        InitializeComponent();
        Body.Text = text ?? "";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Body.Text.Length == 0)
            return;
        Clipboard.SetText(Body.Text);
    }
}
