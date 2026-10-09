using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class AlertWidgetWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly TimeSpan TopmostRefresh = TimeSpan.FromSeconds(4);
    private const double IndicatorStrip = 4;
    private const double PickupSlaStrip = 10;
    private const double WidgetFallbackWidth = 280;
    private const double WidgetFallbackHeight = 44;

    private readonly DispatcherTimer _jiggleTimer;
    private readonly DispatcherTimer _topmostTimer;
    private readonly AlertJiggleSchedule _schedule = new();
    private readonly AlertWidgetMotion _motion = new();
    private readonly AlertSound _sound = new();
    private NotificationWorkspaceViewModel? _model;
    private NotificationSettingsViewModel? _settings;
    private bool _mainMinimized;
    private IReadOnlyList<AlertKind>? _pendingCauses;
    private bool _pendingPickupSla;
    private DispatcherTimer? _dropTimer;
    private bool _allowClose;
    private bool _opening;
    private bool _wiggling;
    private bool _positioning;
    private int _dropGeneration;

    public AlertWidgetWindow()
    {
        InitializeComponent();
        ShowActivated = false;
        Topmost = true;
        _jiggleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _jiggleTimer.Tick += (_, _) => BeginIntervalDrop();
        _topmostTimer = new DispatcherTimer { Interval = TopmostRefresh };
        _topmostTimer.Tick += (_, _) => RefreshTopmost();
    }

    public event EventHandler<AlertKind>? Opened;

    public void Attach(NotificationWorkspaceViewModel model, NotificationSettingsViewModel settings)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(settings);
        if (_model is not null)
        {
            _model.Attention -= OnAttention;
            _model.PropertyChanged -= OnModelPropertyChanged;
        }
        if (_settings is not null)
            _settings.PropertyChanged -= OnSettingsChanged;

        _model = model;
        _settings = settings;
        DataContext = model;
        model.Attention += OnAttention;
        model.PropertyChanged += OnModelPropertyChanged;
        settings.PropertyChanged += OnSettingsChanged;
        EnsureTimer();
        ApplyPresence();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotificationWorkspaceViewModel.HasPickupSla)
            or nameof(NotificationWorkspaceViewModel.PickupLabel)
            or nameof(NotificationWorkspaceViewModel.PickupBarPercent))
            ApplyChrome();
    }

    public void SetMainMinimized(bool minimized)
    {
        _mainMinimized = minimized;
        ApplyPresence();
    }

    public void Shutdown()
    {
        _allowClose = true;
        _schedule.Stop();
        _jiggleTimer.Stop();
        _topmostTimer.Stop();
        _dropTimer?.Stop();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
        PinTopmost();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            return;
        }

        _jiggleTimer.Stop();
        _topmostTimer.Stop();
        _dropTimer?.Stop();
        base.OnClosing(e);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationSettingsViewModel.ActiveFrequency))
            EnsureTimer();
        if (e.PropertyName is nameof(NotificationSettingsViewModel.ActiveShowDesktopWidget)
            or nameof(NotificationSettingsViewModel.ActiveJiggleWhen))
            ApplyPresence();
    }

    private void OnAttention(object? sender, AlertAttention attention)
    {
        if (attention.PlaySound && _settings is { ActivePlaySoundOnAlertMetric: true })
            _sound.Play(_settings.ActiveSoundPath);

        if (_settings is null || _model is null)
            return;

        if (attention.PickupSlaThreshold)
            PresentPickupSla(_settings.ActivePlaySoundWhenJiggling);

        var decision = AlertJiggleRules.IncreaseDue(
            _settings.ActiveJiggleWhen,
            new AlertPollDecision(attention.Increased));
        Present(decision, _settings.ActivePlaySoundWhenJiggling);
    }

    private void PresentPickupSla(bool playSound)
    {
        if (_model is null || _settings is null)
            return;

        if (!DesktopAllowed() || !IsVisible)
        {
            _pendingPickupSla = true;
            return;
        }

        _pendingPickupSla = false;
        BeginPickupDrop(playSound);
    }

    private bool DesktopAllowed() =>
        AlertJiggleRules.AllowsDesktopWidget(
            _settings?.ActiveShowDesktopWidget ?? DesktopWidgetWhen.WhileOpen,
            _mainMinimized);

    private void ApplyPresence()
    {
        if (!DesktopAllowed())
        {
            _motion.SetPointerOver(false);
            _motion.SetTimerDrop(false);
            _dropTimer?.Stop();
            if (IsVisible)
                Hide();
            EnsureTopmostTimer();
            return;
        }

        if (_model is null)
            return;

        if (!IsVisible)
        {
            Opacity = 0;
            ShowActivated = false;
            Topmost = true;
            Show();
            UpdateLayout();
            Opacity = 1;
        }

        PinTopmost();
        EnsureTopmostTimer();
        ApplyChrome();
        FlushPending();
        FlushPendingPickup();
    }

    private void FlushPendingPickup()
    {
        if (!_pendingPickupSla || _model is null || _settings is null || !DesktopAllowed())
            return;
        if (!_model.HasPickupSla)
        {
            _pendingPickupSla = false;
            return;
        }

        _pendingPickupSla = false;
        BeginPickupDrop(_settings.ActivePlaySoundWhenJiggling);
    }

    private void BeginPickupDrop(bool playSound)
    {
        if (_settings is null || _model is null || !IsVisible || !_model.HasPickupSla)
            return;

        _model.ShowPickupJiggle(true);
        if (playSound)
            _sound.Play(_settings.ActiveSoundPath);

        var maximize = _settings.ActiveMaximizeWhenJiggling;
        _motion.SetTimerDrop(maximize);
        var generation = ++_dropGeneration;
        ApplyChrome();
        if (maximize)
            StartWiggle(generation, _settings.ActiveJiggleSpeed);
        PinTopmost();

        _dropTimer?.Stop();
        var seconds = Math.Max(1, _settings.ActiveDurationSeconds);
        var hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _dropTimer = hold;
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            if (generation != _dropGeneration)
                return;
            _motion.SetTimerDrop(false);
            _model.ShowPickupJiggle(false);
            ApplyChrome();
        };
        hold.Start();
    }

    private void BeginIntervalDrop()
    {
        if (_settings is null || _model is null)
            return;

        var decision = AlertJiggleRules.IntervalDue(_settings.ActiveJiggleWhen, _model.UnacknowledgedKinds);
        Present(decision, _settings.ActivePlaySoundWhenJiggling);
    }

    private void Present(AlertJiggleDecision decision, bool playSound)
    {
        var causes = AlertJiggleRules.HighlightedKinds(decision);
        if (causes.Count == 0)
            return;

        if (!DesktopAllowed() || !IsVisible)
        {
            _pendingCauses = causes;
            return;
        }

        _pendingCauses = null;
        BeginDrop(decision, playSound);
    }

    private void FlushPending()
    {
        if (_pendingCauses is not { Count: > 0 } pending || _model is null || _settings is null || !DesktopAllowed())
            return;

        var still = pending.Where(kind => _model.UnacknowledgedKinds.Contains(kind)).ToArray();
        _pendingCauses = null;
        if (still.Length == 0)
            return;

        BeginDrop(new AlertJiggleDecision(still), _settings.ActivePlaySoundWhenJiggling);
    }

    private void EnsureTimer()
    {
        var interval = _settings?.ActiveJiggleInterval ?? TimeSpan.FromMinutes(1);
        if (!_schedule.Arm(interval))
            return;

        _jiggleTimer.Stop();
        _jiggleTimer.Interval = _schedule.Interval;
        _jiggleTimer.Start();
    }

    private void BeginDrop(AlertJiggleDecision decision, bool playSound)
    {
        var causes = AlertJiggleRules.HighlightedKinds(decision);
        if (causes.Count == 0 || _settings is null || _model is null || !IsVisible)
            return;

        _model.ShowJiggle(causes);
        if (playSound)
            _sound.Play(_settings.ActiveSoundPath);

        var maximize = _settings.ActiveMaximizeWhenJiggling;
        _motion.SetTimerDrop(maximize);
        var generation = ++_dropGeneration;
        ApplyChrome();
        if (maximize)
            StartWiggle(generation, _settings.ActiveJiggleSpeed);
        PinTopmost();

        _dropTimer?.Stop();
        var seconds = Math.Max(1, _settings.ActiveDurationSeconds);
        var hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _dropTimer = hold;
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            if (generation != _dropGeneration)
                return;
            _motion.SetTimerDrop(false);
            _model.ShowPickupJiggle(false);
            ApplyChrome();
        };
        hold.Start();
    }

    private void StartWiggle(int generation, double movesPerSecond)
    {
        var origin = CenterLeft(Width > 1 ? Width : WidgetFallbackWidth);
        BeginAnimation(LeftProperty, null);
        Left = origin;
        var seconds = Math.Max(1, _settings?.ActiveDurationSeconds ?? 2);
        var duration = TimeSpan.FromSeconds(seconds);
        var plan = JiggleMotion.Plan(movesPerSecond, duration);
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        var frames = JiggleMotion.KeyframeCount(plan.SineCycles);
        for (var i = 0; i <= frames; i++)
        {
            var t = i / (double)frames;
            var offset = JiggleMotion.Offset(plan.MovesPerSecond, duration, t);
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(origin + offset, KeyTime.FromPercent(t)));
        }

        _wiggling = true;
        animation.Completed += (_, _) =>
        {
            if (generation != _dropGeneration)
                return;
            _wiggling = false;
            BeginAnimation(LeftProperty, null);
            Left = CenterLeft(Width > 1 ? Width : WidgetFallbackWidth);
        };
        BeginAnimation(LeftProperty, animation);
    }

    private void ApplyChrome()
    {
        if (_positioning || !IsVisible)
            return;

        _positioning = true;
        try
        {
            // Measure with the drop panel present so the strip stays as wide as the labels.
            // Then hide that panel while the window is only the 4px strip. Its 1px line border
            // and white fill otherwise paint as an empty collapsed box over the desk.
            if (DropPanel.Visibility != Visibility.Visible)
                DropPanel.Visibility = Visibility.Visible;
            WidgetRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = WidgetRoot.DesiredSize;
            var width = desired.Width > 1 ? desired.Width : WidgetFallbackWidth;
            var openHeight = desired.Height > 1 ? desired.Height : WidgetFallbackHeight;
            var barOpen = _motion.BarOpen;
            DropPanel.Visibility = barOpen ? Visibility.Visible : Visibility.Collapsed;
            Width = width;
            var restHeight = IndicatorStrip + (_model is { HasPickupSla: true } ? PickupSlaStrip : 0);
            Height = barOpen ? openHeight : restHeight;
            if (!_wiggling)
            {
                BeginAnimation(LeftProperty, null);
                Left = CenterLeft(width);
            }

            BeginAnimation(TopProperty, null);
            Top = SystemParameters.WorkArea.Top;
        }
        finally
        {
            _positioning = false;
        }
    }

    private double CenterLeft(double width)
    {
        var area = SystemParameters.WorkArea;
        var left = area.Left + Math.Max(0, (area.Width - width) / 2);
        var maxLeft = area.Right - width;
        return left > maxLeft ? Math.Max(area.Left, maxLeft) : left;
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!DesktopAllowed())
            return;
        _motion.SetPointerOver(true);
        ApplyChrome();
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!DesktopAllowed())
            return;
        _motion.SetPointerOver(false);
        ApplyChrome();
    }

    private void Circle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AlertCircleModel circle })
            OpenFromCircle(circle.Kind);
        e.Handled = true;
    }

    private void PickupSla_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null)
            return;
        if (_model.OpenPickupCommand.CanExecute(null))
            _model.OpenPickupCommand.Execute(null);
        e.Handled = true;
    }

    private void RefreshTopmost()
    {
        if (!IsVisible)
        {
            _topmostTimer.Stop();
            return;
        }

        PinTopmost();
    }

    private void EnsureTopmostTimer()
    {
        if (!IsVisible)
        {
            _topmostTimer.Stop();
            return;
        }

        if (!_topmostTimer.IsEnabled)
            _topmostTimer.Start();
    }

    // Topmost is dropped when another window is activated later. Put it back without focusing or moving.
    private void PinTopmost()
    {
        if (!IsVisible)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
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

    [DllImport("user32.dll", SetLastError = false)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
