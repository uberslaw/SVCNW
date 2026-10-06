using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ServiceNowDesk.Views;

public enum HelpTopic
{
    Authentication,
    Notifications
}

public partial class HelpWindow : Window
{
    public HelpWindow(HelpTopic topic = HelpTopic.Authentication)
    {
        InitializeComponent();
        ShowTopic(topic);
    }

    public void ShowTopic(HelpTopic topic)
    {
        var notifications = topic == HelpTopic.Notifications;
        AuthenticationPanel.Visibility = notifications ? Visibility.Collapsed : Visibility.Visible;
        NotificationsPanel.Visibility = notifications ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = notifications ? "Notifications" : "Authentication Setup";
        Title = notifications ? "Help — Notifications" : "Help — Authentication Setup";
        MarkTopic(AuthTopicButton, !notifications);
        MarkTopic(NotificationsTopicButton, notifications);
        HelpScroll.ScrollToTop();
    }

    private void AuthTopic_Click(object sender, RoutedEventArgs e) => ShowTopic(HelpTopic.Authentication);

    private void NotificationsTopic_Click(object sender, RoutedEventArgs e) => ShowTopic(HelpTopic.Notifications);

    private static void MarkTopic(Button button, bool selected)
    {
        button.Background = (Brush)button.FindResource(selected ? "AccentBrush" : "CardBrush");
        button.Foreground = (Brush)button.FindResource(selected ? "WhiteBrush" : "TextBrush");
        button.BorderBrush = (Brush)button.FindResource(selected ? "AccentBrush" : "LineBrush");
    }
}
