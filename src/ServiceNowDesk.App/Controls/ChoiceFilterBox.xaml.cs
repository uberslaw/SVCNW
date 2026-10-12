using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ServiceNowDesk.Controls;

public partial class ChoiceFilterBox
{
    public static readonly DependencyProperty CountTextProperty = DependencyProperty.Register(
        nameof(CountText),
        typeof(string),
        typeof(ChoiceFilterBox),
        new PropertyMetadata("0"));

    public static readonly DependencyProperty FilterNameProperty = DependencyProperty.Register(
        nameof(FilterName),
        typeof(string),
        typeof(ChoiceFilterBox),
        new PropertyMetadata(""));

    public static readonly DependencyProperty ClearNameProperty = DependencyProperty.Register(
        nameof(ClearName),
        typeof(string),
        typeof(ChoiceFilterBox),
        new PropertyMetadata("Clear filters"));

    public static readonly DependencyProperty ClosedCharactersProperty = DependencyProperty.Register(
        nameof(ClosedCharacters),
        typeof(int),
        typeof(ChoiceFilterBox),
        new PropertyMetadata(0, (d, _) => ((ChoiceFilterBox)d).ApplyClosedWidth()));

    public static readonly DependencyProperty ClosedLabelProperty = DependencyProperty.Register(
        nameof(ClosedLabel),
        typeof(string),
        typeof(ChoiceFilterBox),
        new PropertyMetadata("0"));

    private bool _applying;
    private bool _closedByThisClick;

    public ChoiceFilterBox()
    {
        InitializeComponent();
        ChoicePopup.CustomPopupPlacementCallback = PlaceUnderButton;
        Loaded += (_, _) => ApplyClosedWidth();
    }

    /// <summary>
    /// Closed width in characters. Zero stretches with the column.
    /// One selected value is trimmed to this width.
    /// </summary>
    public int ClosedCharacters
    {
        get => (int)GetValue(ClosedCharactersProperty);
        set => SetValue(ClosedCharactersProperty, value);
    }

    public string ClosedLabel
    {
        get => (string)GetValue(ClosedLabelProperty);
        private set => SetValue(ClosedLabelProperty, value);
    }

    public string CountText
    {
        get => (string)GetValue(CountTextProperty);
        set => SetValue(CountTextProperty, value);
    }

    public string FilterName
    {
        get => (string)GetValue(FilterNameProperty);
        set => SetValue(FilterNameProperty, value);
    }

    public string ClearName
    {
        get => (string)GetValue(ClearNameProperty);
        set => SetValue(ClearNameProperty, value);
    }

    public IReadOnlyList<string> SelectedValues { get; private set; } = [];

    public event EventHandler? SelectionChanged;

    public void SetItems(IReadOnlyList<string>? items, IReadOnlyList<string>? selected)
    {
        items ??= [];
        selected ??= [];
        if (SameItems(items))
        {
            if (SameSelection(selected))
                return;
            ApplySelection(selected);
            return;
        }

        _applying = true;
        try
        {
            ChoiceList.Items.Clear();
            foreach (var item in items)
                ChoiceList.Items.Add(item);
            ApplySelectionCore(selected);
            EmptyHint.Visibility = ChoiceList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplySelection(IReadOnlyList<string> selected)
    {
        _applying = true;
        try
        {
            ApplySelectionCore(selected);
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplySelectionCore(IReadOnlyList<string> selected)
    {
        var wanted = new HashSet<string>(
            selected.Select(value => (value ?? "").Trim()).Where(value => value.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        ChoiceList.SelectedItems.Clear();
        foreach (var item in ChoiceList.Items)
        {
            if (item is string text && wanted.Contains(text))
                ChoiceList.SelectedItems.Add(item);
        }

        SelectedValues = ChoiceList.SelectedItems.Cast<string>().ToArray();
        RefreshClosedLabel();
    }

    private void RefreshClosedLabel()
    {
        ClosedLabel = SelectedValues.Count == 1
            ? SelectedValues[0]
            : SelectedValues.Count.ToString(CultureInfo.InvariantCulture);
    }

    private void ApplyClosedWidth()
    {
        if (ClosedCharacters <= 0)
        {
            ClearValue(WidthProperty);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            OpenButton.ClearValue(WidthProperty);
            return;
        }

        var buttonWidth = MeasureClosedText() + 32;
        OpenButton.Width = buttonWidth;
        Width = buttonWidth + 28;
        HorizontalAlignment = HorizontalAlignment.Left;
    }

    private double MeasureClosedText()
    {
        var count = Math.Max(1, ClosedCharacters);
        var size = OpenButton.FontSize > 0 ? OpenButton.FontSize : 12;
        var dpi = 1.0;
        if (IsLoaded)
            dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(
            new string('0', count),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            Brushes.Black,
            dpi);
        return Math.Ceiling(text.Width);
    }

    private static CustomPopupPlacement[] PlaceUnderButton(Size popupSize, Size targetSize, Point offset) =>
        [new CustomPopupPlacement(new Point(0, targetSize.Height), PopupPrimaryAxis.None)];

    private bool SameItems(IReadOnlyList<string> items)
    {
        if (ChoiceList.Items.Count != items.Count)
            return false;
        for (var i = 0; i < items.Count; i++)
        {
            if (!string.Equals(ChoiceList.Items[i] as string, items[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private bool SameSelection(IReadOnlyList<string> selected)
    {
        var current = ChoiceList.SelectedItems.Cast<string>().ToArray();
        if (current.Length != selected.Count)
            return false;
        var wanted = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        return current.All(wanted.Contains);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_closedByThisClick)
        {
            _closedByThisClick = false;
            return;
        }

        var width = OpenButton.ActualWidth;
        if (width < 1)
            width = ActualWidth;
        if (width < 1)
            width = 120;
        DropBorder.Width = width;
        DropBorder.MinWidth = width;
        DropBorder.MaxWidth = width;
        ChoicePopup.IsOpen = true;
        ChoiceList.Focus();
        if (ChoiceList.SelectedItems.Count > 0)
            ChoiceList.ScrollIntoView(ChoiceList.SelectedItems[0]!);
    }

    private void Drop_Closed(object? sender, EventArgs e)
    {
        _closedByThisClick = true;
        Dispatcher.BeginInvoke(() => _closedByThisClick = false);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (ChoiceList.SelectedItems.Count == 0)
        {
            if (SelectedValues.Count == 0)
                return;
            SelectedValues = [];
            RefreshClosedLabel();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        ChoiceList.SelectedItems.Clear();
    }

    private void ChoiceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying)
            return;
        SelectedValues = ChoiceList.SelectedItems.Cast<string>().ToArray();
        RefreshClosedLabel();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
