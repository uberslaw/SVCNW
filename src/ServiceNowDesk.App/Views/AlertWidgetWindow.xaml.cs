using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class AlertWidgetWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const double WidgetFallbackWidth = 280;
    private const double WidgetFallbackHeight = 44;

    private readonly DispatcherTimer _jiggleTimer;
    private readonly AlertSound _sound = new();
    private NotificationWorkspaceViewModel? _model;
    private bool _mainMinimized;
    private bool _jiggling;
    private bool _allowClose;
    private bool _opening;
    private int _jiggleGeneration;

    public AlertWidgetWindow()
    {
        InitializeComponent();
        _jiggleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _jiggleTimer.Tick += (_, _) =>
        {
            if (_model is null || !_mainMinimized || !_model.AnyUnacknowledged)
                return;
            BeginJiggle(_model.ActivePlaySoundWhenJiggling);
        };
        SizeChanged += (_, _) =>
        {
            if (IsVisible)
                PlaceOnScreen();
        };
    }

    public event EventHandler<AlertKind>? Opened;

    public void Attach(NotificationWorkspaceViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.Attention -= OnAttention;
        }

        _model = model;
        DataContext = model;
        model.PropertyChanged += OnModelChanged;
        model.Attention += OnAttention;
        UpdatePresence();
    }

    public void SetMainMinimized(bool minimized)
    {
        _mainMinimized = minimized;
        if (!minimized)
            _jiggling = false;

        UpdatePresence();
    }

    public void Shutdown()
    {
        _allowClose = true;
        _jiggleTimer.Stop();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            _jiggleTimer.Stop();
            return;
        }

        base.OnClosing(e);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotificationWorkspaceViewModel.AnyUnacknowledged)
            or nameof(NotificationWorkspaceViewModel.ActiveFrequency)
            or nameof(NotificationWorkspaceViewModel.ActiveDurationSeconds)
            or nameof(NotificationWorkspaceViewModel.ActiveMaximizeWhenJiggling)
            or nameof(NotificationWorkspaceViewModel.ActivePlaySoundWhenJiggling)
            or nameof(NotificationWorkspaceViewModel.ActiveSoundPath))
        {
            UpdatePresence();
        }
    }

    private void OnAttention(object? sender, AlertAttention attention)
    {
        if (attention.PlaySound)
            _sound.Play(_model?.ActiveSoundPath);

        if (_mainMinimized && _model is { AnyUnacknowledged: true })
        {
            UpdatePresence();
            BeginJiggle(playSound: false);
        }
    }

    private void UpdatePresence()
    {
        var show = _mainMinimized
            && _model is { AnyUnacknowledged: true }
            && _model.Circles.Any(circle => circle.IsVisible);
        if (!show)
        {
            _jiggleTimer.Stop();
            if (IsVisible)
                Hide();
            return;
        }

        if (!IsVisible)
        {
            Opacity = 0;
            ShowActivated = false;
            Show();
            UpdateLayout();
            PlaceOnScreen();
            Opacity = 1;
        }
        else
        {
            PlaceOnScreen();
        }

        EnsureTimer();
    }

    private void EnsureTimer()
    {
        var interval = _model?.ActiveJiggleInterval ?? TimeSpan.FromMinutes(1);
        if (interval <= TimeSpan.Zero)
            interval = TimeSpan.FromMinutes(1);
        if (_jiggleTimer.Interval != interval)
        {
            var running = _jiggleTimer.IsEnabled;
            _jiggleTimer.Stop();
            _jiggleTimer.Interval = interval;
            if (running)
                _jiggleTimer.Start();
        }

        if (!_jiggleTimer.IsEnabled)
            _jiggleTimer.Start();
    }

    private void BeginJiggle(bool playSound)
    {
        if (_model is null || !IsVisible)
            return;

        if (playSound)
            _sound.Play(_model.ActiveSoundPath);

        PlaceOnScreen();

        var generation = ++_jiggleGeneration;
        _jiggling = true;
        var origin = CenterLeft();
        BeginAnimation(LeftProperty, null);
        Left = origin;
        var seconds = Math.Max(1, _model.ActiveDurationSeconds);
        var duration = TimeSpan.FromSeconds(seconds);
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        const int cycles = 6;
        var frames = cycles * 8;
        for (var i = 0; i <= frames; i++)
        {
            var t = i / (double)frames;
            var offset = Math.Sin(t * cycles * Math.PI * 2) * 6;
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(origin + offset, KeyTime.FromPercent(t)));
        }

        animation.Completed += (_, _) =>
        {
            if (generation != _jiggleGeneration)
                return;
            _jiggling = false;
            BeginAnimation(LeftProperty, null);
            PlaceOnScreen();
        };
        BeginAnimation(LeftProperty, animation);
        RestartJiggleTimer();
    }

    private void RestartJiggleTimer()
    {
        if (!_jiggleTimer.IsEnabled)
            return;
        _jiggleTimer.Stop();
        _jiggleTimer.Start();
    }

    private void PlaceOnScreen()
    {
        if (!_jiggling)
        {
            BeginAnimation(LeftProperty, null);
            Left = CenterLeft();
        }

        BeginAnimation(TopProperty, null);
        Top = RestTop();
    }

    private double RestTop()
    {
        var area = SystemParameters.WorkArea;
        var height = ActualHeight > 1 ? ActualHeight : WidgetFallbackHeight;
        var top = area.Top + 8;
        var maxTop = area.Bottom - height;
        return top > maxTop ? Math.Max(area.Top, maxTop) : top;
    }

    private double CenterLeft()
    {
        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 1 ? ActualWidth : WidgetFallbackWidth;
        var left = area.Left + Math.Max(0, (area.Width - width) / 2);
        var maxLeft = area.Right - width;
        return left > maxLeft ? Math.Max(area.Left, maxLeft) : left;
    }

    private void Circle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AlertCircleModel circle })
            OpenFromCircle(circle.Kind);
        e.Handled = true;
    }

    private void OpenFromCircle(AlertKind kind)
    {
        if (_opening)
            return;
        _opening = true;
        Opened?.Invoke(this, kind);
        Dispatcher.BeginInvoke(() => _opening = false, DispatcherPriority.Background);
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index) : new IntPtr(GetWindowLong32(hWnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, index, value) : new IntPtr(SetWindowLong32(hWnd, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);
}
