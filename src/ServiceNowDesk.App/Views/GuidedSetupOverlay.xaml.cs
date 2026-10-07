using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ServiceNowDesk.GuidedSetup;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

/// <summary>
/// Spotlight on the main window. A separate always-on-top window would sit under or over the notification strip.
/// </summary>
public partial class GuidedSetupOverlay : UserControl
{
    private readonly RectangleGeometry _window = new();
    private readonly RectangleGeometry _hole = new();
    private GuidedSetupController? _guided;
    private ScrollViewer? _connectionScroll;
    private GuidedSetupPhase _visualPhase = GuidedSetupPhase.Idle;
    private DeskSection _visualTab;
    private int _generation;
    private int _methodPulseGeneration;
    private int _buttonPulseGeneration;
    private bool _animating;
    private bool _deferSignInCopy;
    private Rect _lastSnap = Rect.Empty;

    public GuidedSetupOverlay()
    {
        InitializeComponent();
        DimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, _window, _hole);
        Loaded += (_, _) => Attach();
        DataContextChanged += (_, _) => Attach();
        SizeChanged += (_, _) => OnSized();
    }

    private void Attach()
    {
        if (_guided is not null)
            _guided.PropertyChanged -= OnGuidedChanged;
        _guided = (DataContext as MainViewModel)?.Guided;
        if (_guided is not null)
            _guided.PropertyChanged += OnGuidedChanged;
        _visualPhase = GuidedSetupPhase.Idle;
        Dispatcher.BeginInvoke(SyncVisual, DispatcherPriority.Loaded);
    }

    private void OnGuidedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(GuidedSetupController.Phase)
            or nameof(GuidedSetupController.HighlightedTab)
            or nameof(GuidedSetupController.ShowTour)
            or nameof(GuidedSetupController.ShowChrome)))
            return;
        Dispatcher.BeginInvoke(SyncVisual, DispatcherPriority.Loaded);
    }

    private void OnSized()
    {
        UpdateWindowGeometry();
        if (_animating || _guided is not { ShowTour: true })
            return;
        SnapToTarget();
    }

    private void SyncVisual()
    {
        if (_guided is null)
            return;
        if (!_guided.ShowTour)
        {
            HideTourVisuals();
            _visualPhase = GuidedSetupPhase.Idle;
            return;
        }

        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        UpdateWindowGeometry();
        WatchConnectionScroll();
        if (_guided.Phase != _visualPhase)
        {
            _visualPhase = _guided.Phase;
            switch (_guided.Phase)
            {
                case GuidedSetupPhase.Connection:
                    PlayConnectionIntro();
                    break;
                case GuidedSetupPhase.SignIn:
                    PlaySignIn();
                    break;
                case GuidedSetupPhase.Splash:
                    PlaySplash();
                    break;
                case GuidedSetupPhase.Tabs:
                    PlayFirstTab();
                    break;
                default:
                    HideTourVisuals();
                    break;
            }

            return;
        }

        if (_guided.Phase == GuidedSetupPhase.Tabs && _visualTab != _guided.HighlightedTab)
            MoveToNextTab();
    }

    private void PlayConnectionIntro()
    {
        var generation = ++_generation;
        _animating = true;
        _deferSignInCopy = false;
        StopButtonPulse();
        DimPath.IsHitTestVisible = true;
        InstructionChip.Visibility = Visibility.Visible;
        TabChip.Visibility = Visibility.Collapsed;
        var method = SignInMethodBounds(reveal: true);
        PositionInstruction(method);
        var combo = ComboBounds();
        if (!combo.IsEmpty)
            StartMethodPulse(combo);

        var dim = new DoubleAnimation(0, GuidedSetupMotion.ConnectionDim, GuidedSetupMotion.DimDuration);
        dim.Completed += (_, _) =>
        {
            if (generation != _generation)
                return;
            var from = CenterBox(0, 0);
            var small = CenterBox(120, 72);
            AnimateHole(generation, from, small, GuidedSetupMotion.GrowDuration, () =>
            {
                var target = SignInMethodBounds(reveal: false);
                AnimateHole(generation, small, target, GuidedSetupMotion.MoveDuration, () =>
                {
                    _animating = false;
                    _lastSnap = target;
                    PositionInstruction(target);
                    var ring = ComboBounds();
                    if (!ring.IsEmpty)
                        PositionRing(ring);
                });
            });
        };
        DimPath.BeginAnimation(OpacityProperty, dim);
    }

    private void PlaySignIn()
    {
        var generation = ++_generation;
        _animating = true;
        _deferSignInCopy = true;
        StopMethodPulse();
        InstructionChip.Visibility = Visibility.Collapsed;
        TabChip.Visibility = Visibility.Collapsed;
        DimPath.IsHitTestVisible = true;
        var current = _hole.Rect.IsEmpty ? CenterBox(0, 0) : _hole.Rect;
        var collapsed = new Rect(current.X + current.Width / 2, current.Y + current.Height / 2, 0, 0);
        AnimateHole(generation, current, collapsed, TimeSpan.FromSeconds(0.4), () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation)
                    return;
                var button = SignInButtonBounds(reveal: true);
                AnimateHole(generation, collapsed, button, GuidedSetupMotion.MoveDuration, () =>
                {
                    _animating = false;
                    _deferSignInCopy = false;
                    _lastSnap = button;
                    InstructionChip.Visibility = Visibility.Visible;
                    PositionInstruction(button);
                    StartButtonPulse();
                });
            }, DispatcherPriority.Loaded);
        });
    }

    private void PlaySplash()
    {
        _generation++;
        _animating = false;
        _deferSignInCopy = false;
        StopMethodPulse();
        StopButtonPulse();
        _hole.BeginAnimation(RectangleGeometry.RectProperty, null);
        _hole.Rect = new Rect(0, 0, 0, 0);
        TabChip.Visibility = Visibility.Collapsed;
        var fade = new DoubleAnimation(DimPath.Opacity, 0, TimeSpan.FromSeconds(0.35));
        fade.Completed += (_, _) => DimPath.IsHitTestVisible = false;
        DimPath.BeginAnimation(OpacityProperty, fade);
        InstructionChip.Visibility = Visibility.Visible;
        PositionSplash();
    }

    private void PlayFirstTab()
    {
        var generation = ++_generation;
        _animating = true;
        _deferSignInCopy = false;
        StopMethodPulse();
        StopButtonPulse();
        InstructionChip.Visibility = Visibility.Collapsed;
        DimPath.IsHitTestVisible = true;
        _visualTab = _guided?.HighlightedTab ?? DeskSection.DailyWork;
        var target = TabBounds(reveal: true);
        TabChip.Visibility = Visibility.Visible;
        PositionTab(target);
        var dim = new DoubleAnimation(DimPath.Opacity, GuidedSetupMotion.TabDim, TimeSpan.FromSeconds(0.3));
        DimPath.BeginAnimation(OpacityProperty, dim);
        var from = new Rect(target.X + target.Width / 2, target.Y + target.Height / 2, 0, 0);
        AnimateHole(generation, from, target, GuidedSetupMotion.GrowDuration, () =>
        {
            _animating = false;
            _lastSnap = target;
            PositionTab(target);
        });
    }

    private void MoveToNextTab()
    {
        if (_guided is null)
            return;
        var generation = ++_generation;
        _visualTab = _guided.HighlightedTab;
        var target = TabBounds(reveal: true);
        TabChip.Visibility = Visibility.Visible;
        PositionTab(target);
        var from = _hole.Rect.IsEmpty ? target : _hole.Rect;
        AnimateHole(generation, from, target, GuidedSetupMotion.MoveDuration, () =>
        {
            _lastSnap = target;
            PositionTab(target);
        });
    }

    private void SnapToTarget()
    {
        if (_guided is null || _animating)
            return;
        switch (_guided.Phase)
        {
            case GuidedSetupPhase.Connection:
                var method = SignInMethodBounds(reveal: false);
                if (Nearly(_lastSnap, method))
                    return;
                _lastSnap = method;
                SetHole(method);
                PositionInstruction(method);
                var combo = ComboBounds();
                if (!combo.IsEmpty)
                    PositionRing(combo);
                break;
            case GuidedSetupPhase.SignIn:
                var button = SignInButtonBounds(reveal: false);
                if (Nearly(_lastSnap, button))
                    return;
                _lastSnap = button;
                SetHole(button);
                if (!_deferSignInCopy)
                {
                    InstructionChip.Visibility = Visibility.Visible;
                    PositionInstruction(button);
                }
                break;
            case GuidedSetupPhase.Tabs:
                var tab = TabBounds(reveal: false);
                if (Nearly(_lastSnap, tab))
                    return;
                _lastSnap = tab;
                SetHole(tab);
                PositionTab(tab);
                break;
            case GuidedSetupPhase.Splash:
                PositionSplash();
                break;
        }
    }

    private void AnimateHole(int generation, Rect from, Rect to, TimeSpan duration, Action completed)
    {
        _animating = true;
        if (to.IsEmpty)
            to = CenterBox(120, 72);
        _hole.BeginAnimation(RectangleGeometry.RectProperty, null);
        _hole.Rect = from;
        var animation = new RectAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (generation != _generation)
                return;
            _hole.BeginAnimation(RectangleGeometry.RectProperty, null);
            _hole.Rect = to;
            completed();
        };
        _hole.BeginAnimation(RectangleGeometry.RectProperty, animation);
    }

    private void SetHole(Rect rect)
    {
        _hole.BeginAnimation(RectangleGeometry.RectProperty, null);
        _hole.Rect = rect.IsEmpty ? CenterBox(120, 72) : rect;
    }

    private void StartMethodPulse(Rect combo)
    {
        var generation = ++_methodPulseGeneration;
        PositionRing(combo);
        PulseRing.Visibility = Visibility.Visible;
        PulseRing.BeginAnimation(OpacityProperty, null);
        var half = TimeSpan.FromSeconds(1d / (GuidedSetupMotion.PulsePerSecond * 2d));
        var pulse = new DoubleAnimation(1, 0.28, half)
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(GuidedSetupMotion.SignInMethodPulse)
        };
        pulse.Completed += (_, _) =>
        {
            if (generation != _methodPulseGeneration)
                return;
            PulseRing.BeginAnimation(OpacityProperty, null);
            PulseRing.Visibility = Visibility.Collapsed;
        };
        PulseRing.BeginAnimation(OpacityProperty, pulse);
    }

    private void StopMethodPulse()
    {
        _methodPulseGeneration++;
        PulseRing.BeginAnimation(OpacityProperty, null);
        PulseRing.Visibility = Visibility.Collapsed;
    }

    private void StartButtonPulse()
    {
        var button = SignInButton();
        if (button is null)
            return;
        var generation = ++_buttonPulseGeneration;
        button.BeginAnimation(OpacityProperty, null);
        var half = TimeSpan.FromSeconds(1d / (GuidedSetupMotion.PulsePerSecond * 2d));
        var pulse = new DoubleAnimation(1, 0.5, half)
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(GuidedSetupMotion.SignInButtonPulse)
        };
        pulse.Completed += (_, _) =>
        {
            if (generation != _buttonPulseGeneration)
                return;
            button.BeginAnimation(OpacityProperty, null);
            button.Opacity = 1;
        };
        button.BeginAnimation(OpacityProperty, pulse);
    }

    private void StopButtonPulse()
    {
        _buttonPulseGeneration++;
        var button = SignInButton();
        if (button is null)
            return;
        button.BeginAnimation(OpacityProperty, null);
        button.Opacity = 1;
    }

    private void HideTourVisuals()
    {
        _generation++;
        _animating = false;
        _deferSignInCopy = false;
        DimPath.BeginAnimation(OpacityProperty, null);
        DimPath.Opacity = 0;
        DimPath.IsHitTestVisible = false;
        _hole.BeginAnimation(RectangleGeometry.RectProperty, null);
        _hole.Rect = new Rect(0, 0, 0, 0);
        StopMethodPulse();
        StopButtonPulse();
        InstructionChip.Visibility = Visibility.Collapsed;
        TabChip.Visibility = Visibility.Collapsed;
        UnwatchConnectionLayout();
    }

    private Rect SignInMethodBounds(bool reveal)
    {
        var page = ConnectionPage();
        if (reveal)
        {
            page?.SignInMethodCaptionControl.BringIntoView();
            page?.SignInMethodComboControl.BringIntoView();
            WatchConnectionLayout(page);
        }

        var bounds = Union(ElementBounds(page?.SignInMethodCaptionControl, 8), ElementBounds(page?.SignInMethodComboControl, 8));
        return bounds.IsEmpty ? CenterBox(120, 72) : bounds;
    }

    private Rect SignInButtonBounds(bool reveal)
    {
        var page = ConnectionPage();
        var button = page?.SignInBrowserButtonControl;
        if (reveal)
        {
            button?.BringIntoView();
            WatchConnectionLayout(page);
        }

        var bounds = ElementBounds(button, 10);
        return bounds.IsEmpty ? CenterBox(220, 64) : bounds;
    }

    private Rect TabBounds(bool reveal)
    {
        var button = FindNavButton(_guided?.HighlightedTab ?? _visualTab);
        if (reveal)
            button?.BringIntoView();
        var bounds = ElementBounds(button, 4);
        return bounds.IsEmpty ? new Rect(8, 120, 200, 44) : bounds;
    }

    private Rect ComboBounds()
    {
        var combo = ConnectionPage()?.SignInMethodComboControl;
        return ElementBounds(combo, 4);
    }

    private void PositionInstruction(Rect target)
    {
        if (_deferSignInCopy)
            return;
        PlaceChip(InstructionChip, target, above: true, preferBeside: false);
    }

    private void PositionTab(Rect target)
    {
        PlaceChip(TabChip, target, above: false, preferBeside: true);
    }

    private void PositionSplash()
    {
        var card = ElementBounds(SplashCard(), 0);
        if (card.IsEmpty)
            card = new Rect(Math.Max(0, (ActualWidth - 680) / 2), Math.Max(0, (ActualHeight - 360) / 2), 680, 360);
        PlaceChip(InstructionChip, card, above: true, preferBeside: false);
    }

    private void PlaceChip(FrameworkElement chip, Rect target, bool above, bool preferBeside)
    {
        chip.Measure(new Size(Math.Max(280, chip.MaxWidth), double.PositiveInfinity));
        var width = Math.Min(chip.MaxWidth, Math.Max(280, chip.DesiredSize.Width));
        if (double.IsNaN(width) || width <= 0)
            width = 420;
        chip.Width = width;
        chip.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Max(48, chip.DesiredSize.Height);
        double x;
        double y;
        if (preferBeside)
        {
            x = target.Right + 16;
            y = target.Top;
            if (x + width > ActualWidth - 16)
                x = Math.Max(16, target.Left);
        }
        else
        {
            x = target.Left + Math.Max(0, (target.Width - width) / 2);
            y = above ? target.Top - height - 14 : target.Bottom + 14;
            if (y < 96)
                y = target.Bottom + 14;
        }

        x = Math.Clamp(x, 16, Math.Max(16, ActualWidth - width - 16));
        y = Math.Clamp(y, 16, Math.Max(16, ActualHeight - height - 16));
        Canvas.SetLeft(chip, x);
        Canvas.SetTop(chip, y);
    }

    private void PositionRing(Rect combo)
    {
        Canvas.SetLeft(PulseRing, combo.Left);
        Canvas.SetTop(PulseRing, combo.Top);
        PulseRing.Width = Math.Max(0, combo.Width);
        PulseRing.Height = Math.Max(0, combo.Height);
    }

    private void UpdateWindowGeometry() =>
        _window.Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));

    private Rect CenterBox(double width, double height) =>
        new(Math.Max(0, (ActualWidth - width) / 2), Math.Max(0, (ActualHeight - height) / 2), Math.Max(0, width), Math.Max(0, height));

    private Rect ElementBounds(FrameworkElement? element, double pad)
    {
        if (element is null || element.ActualWidth <= 0 || element.ActualHeight <= 0 || !element.IsVisible)
            return Rect.Empty;
        try
        {
            var rect = element.TransformToVisual(this).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rect.Inflate(pad, pad);
            return rect;
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    private static Rect Union(Rect left, Rect right)
    {
        if (left.IsEmpty)
            return right;
        if (right.IsEmpty)
            return left;
        return Rect.Union(left, right);
    }

    private static bool Nearly(Rect left, Rect right) =>
        !left.IsEmpty && !right.IsEmpty
        && Math.Abs(left.X - right.X) < 1
        && Math.Abs(left.Y - right.Y) < 1
        && Math.Abs(left.Width - right.Width) < 1
        && Math.Abs(left.Height - right.Height) < 1;

    private MainWindow? Host => Window.GetWindow(this) as MainWindow;

    private ConnectionView? ConnectionPage() => Host?.ConnectionPageControl;

    private FrameworkElement? SplashCard() => Host?.SplashCardControl;

    private Button? SignInButton() => ConnectionPage()?.SignInBrowserButtonControl;

    private RadioButton? FindNavButton(DeskSection section)
    {
        var list = Host?.NavListControl;
        if (list is null)
            return null;
        return FindChildren<RadioButton>(list).FirstOrDefault(button => button.DataContext is DeskNavEntry entry && entry.Section == section);
    }

    private void WatchConnectionScroll()
    {
        var scroller = ConnectionPage() is ConnectionView page ? FindChild<ScrollViewer>(page) : null;
        if (ReferenceEquals(scroller, _connectionScroll))
            return;
        if (_connectionScroll is not null)
            _connectionScroll.ScrollChanged -= OnConnectionScroll;
        _connectionScroll = scroller;
        if (_connectionScroll is not null)
            _connectionScroll.ScrollChanged += OnConnectionScroll;
    }

    private FrameworkElement? _layoutTarget;

    private void WatchConnectionLayout(ConnectionView? page)
    {
        if (page is null || ReferenceEquals(_layoutTarget, page))
            return;
        UnwatchConnectionLayout();
        _layoutTarget = page;
        _layoutTarget.LayoutUpdated += OnConnectionLayout;
    }

    private void UnwatchConnectionLayout()
    {
        if (_layoutTarget is null)
            return;
        _layoutTarget.LayoutUpdated -= OnConnectionLayout;
        _layoutTarget = null;
    }

    private void OnConnectionScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.HorizontalChange == 0)
            return;
        SnapToTarget();
    }

    private void OnConnectionLayout(object? sender, EventArgs e) => SnapToTarget();

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            var nested = FindChild<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var nested in FindChildren<T>(child))
                yield return nested;
        }
    }
}
