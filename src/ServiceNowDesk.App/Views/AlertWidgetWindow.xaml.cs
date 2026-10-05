using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
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
    private const double PeekFraction = 0.15;
    private const double CircleDiameter = 36;

    private readonly DispatcherTimer _jiggleTimer;
    private readonly AlertSound _sound = new();
    private NotificationWorkspaceViewModel? _model;
    private bool _mainMinimized;
    private bool _pointerInside;
    private bool _holdFull;
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
                Reposition(animateTop: false);
        };
    }

    public event EventHandler? Opened;

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
        {
            _pointerInside = false;
            _holdFull = false;
            _jiggling = false;
        }

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
            Reposition(animateTop: false);
            Opacity = 1;
        }
        else
        {
            Reposition(animateTop: false);
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

        var maximize = _model.ActiveMaximizeWhenJiggling;
        _holdFull = maximize;
        if (maximize || _pointerInside)
            Slide(FullTop());

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
            _holdFull = false;
            BeginAnimation(LeftProperty, null);
            Left = CenterLeft();
            if (!_pointerInside)
                Slide(PeekTop());
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

    private void Reposition(bool animateTop)
    {
        if (!_jiggling)
        {
            BeginAnimation(LeftProperty, null);
            Left = CenterLeft();
        }

        var top = _pointerInside || _holdFull ? FullTop() : PeekTop();
        if (animateTop)
            Slide(top);
        else
        {
            BeginAnimation(TopProperty, null);
            Top = top;
        }
    }

    private void Slide(double top)
    {
        var animation = new DoubleAnimation
        {
            To = top,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(TopProperty, animation);
    }

    private double PeekTop()
    {
        var height = ActualHeight > 1 ? ActualHeight : CircleDiameter;
        var visible = CircleDiameter * PeekFraction;
        return SystemParameters.WorkArea.Top - height + visible;
    }

    private double FullTop() => SystemParameters.WorkArea.Top;

    private double CenterLeft()
    {
        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 1 ? ActualWidth : CircleDiameter;
        return area.Left + Math.Max(0, (area.Width - width) / 2);
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _pointerInside = true;
        Slide(FullTop());
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _pointerInside = false;
        if (!_holdFull)
            Slide(PeekTop());
    }

    private void Circle_Click(object sender, RoutedEventArgs e)
    {
        OpenFromCircle();
        e.Handled = true;
    }

    private void OpenFromCircle()
    {
        if (_opening)
            return;
        _opening = true;
        Opened?.Invoke(this, EventArgs.Empty);
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
