using System.Windows;
using System.Windows.Controls;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.Controls;

public partial class FollowUpBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(FollowUpBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private int _second;
    private bool _open;

    public FollowUpBox()
    {
        InitializeComponent();
        for (var hour = 0; hour < 24; hour++)
            HourBox.Items.Add(hour.ToString("00"));
        for (var minute = 0; minute < 60; minute++)
            MinuteBox.Items.Add(minute.ToString("00"));
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void Modify_Click(object sender, RoutedEventArgs e)
    {
        var seed = FollowUpValue.PickerSeed(Text, DateTime.Now);
        _second = seed.Second;
        DayCalendar.SelectedDate = seed.Date;
        DayCalendar.DisplayDate = seed.Date;
        HourBox.SelectedIndex = seed.Hour;
        MinuteBox.SelectedIndex = seed.Minute;
        _open = true;
        PickerPopup.IsOpen = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => PickerPopup.IsOpen = false;

    private void PickerPopup_Closed(object? sender, EventArgs e)
    {
        if (!_open)
            return;
        _open = false;
        var day = DayCalendar.SelectedDate ?? DateTime.Today;
        Text = FollowUpValue.FormatSelection(day, HourBox.SelectedIndex, MinuteBox.SelectedIndex, _second);
    }

    private void Time_DropDownOpened(object? sender, EventArgs e) => PickerPopup.StaysOpen = true;

    private void Time_DropDownClosed(object? sender, EventArgs e) => PickerPopup.StaysOpen = false;
}
