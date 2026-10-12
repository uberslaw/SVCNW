using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Controls;

/// <summary>
/// Presets, Create, Copy details, and Refresh in one equal-cell grid.
/// Button styles stay Chip / Quiet / Secondary. The grid only evens out size and gaps.
/// </summary>
public partial class ListActionBar
{
    public static readonly DependencyProperty CreateTextProperty =
        DependencyProperty.Register(
            nameof(CreateText),
            typeof(string),
            typeof(ListActionBar),
            new PropertyMetadata("", (d, _) => ((ListActionBar)d).Rebuild()));

    public static readonly DependencyProperty CreateCommandProperty =
        DependencyProperty.Register(
            nameof(CreateCommand),
            typeof(ICommand),
            typeof(ListActionBar),
            new PropertyMetadata(null, (d, _) => ((ListActionBar)d).Rebuild()));

    public static readonly DependencyProperty CreateAutomationNameProperty =
        DependencyProperty.Register(
            nameof(CreateAutomationName),
            typeof(string),
            typeof(ListActionBar),
            new PropertyMetadata("", (d, _) => ((ListActionBar)d).Rebuild()));

    private RecordWorkspaceViewModel? _workspace;

    public ListActionBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            HookWorkspace();
            Rebuild();
        };
        Loaded += (_, _) =>
        {
            HookWorkspace();
            Rebuild();
        };
        Unloaded += (_, _) =>
        {
            if (_workspace is not null)
                _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        };
    }

    public string CreateText
    {
        get => (string)GetValue(CreateTextProperty);
        set => SetValue(CreateTextProperty, value);
    }

    public ICommand? CreateCommand
    {
        get => (ICommand?)GetValue(CreateCommandProperty);
        set => SetValue(CreateCommandProperty, value);
    }

    public string CreateAutomationName
    {
        get => (string)GetValue(CreateAutomationNameProperty);
        set => SetValue(CreateAutomationNameProperty, value);
    }

    private void HookWorkspace()
    {
        if (_workspace is not null)
            _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        _workspace = DataContext as RecordWorkspaceViewModel;
        if (_workspace is not null)
            _workspace.PropertyChanged += OnWorkspacePropertyChanged;
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordWorkspaceViewModel.Preset))
            RefreshActive();
    }

    private void Rebuild()
    {
        if (!IsLoaded || Buttons is null)
            return;

        Buttons.Children.Clear();
        if (_workspace is null)
            return;

        var chip = (Style)FindResource("ChipButton");
        var quiet = (Style)FindResource("QuietButton");
        var secondary = (Style)FindResource("SecondaryButton");

        foreach (var preset in _workspace.Presets)
        {
            var button = AddButton(preset.Label, chip, _workspace.ApplyPresetCommand, preset, preset.Label, null, null);
            button.Tag = preset;
            ApplyPresetStyle(button, preset);
        }

        if (!string.IsNullOrWhiteSpace(CreateText) && CreateCommand is not null)
        {
            var name = string.IsNullOrWhiteSpace(CreateAutomationName) ? CreateText : CreateAutomationName;
            AddButton(CreateText, chip, CreateCommand, null, name, null, null);
        }

        if (_workspace.ShowCopyDetails)
        {
            AddButton(
                "Copy details",
                quiet,
                _workspace.CopyDetailsCommand,
                null,
                "Copy details",
                "Copy preset, team, cache, and query details",
                new Thickness(8, 2, 8, 2));
        }

        AddButton(
            "Refresh",
            secondary,
            _workspace.RefreshCommand,
            null,
            "Refresh list",
            "Reload this list from ServiceNow",
            new Thickness(10, 4, 10, 4));
    }

    private Button AddButton(
        string content,
        Style style,
        ICommand command,
        object? parameter,
        string? automationName,
        string? toolTip,
        Thickness? padding)
    {
        var button = new Button
        {
            Content = content,
            Style = style,
            Command = command,
            CommandParameter = parameter,
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = toolTip
        };
        if (padding is Thickness pad)
            button.Padding = pad;
        if (!string.IsNullOrEmpty(automationName))
            AutomationProperties.SetName(button, automationName);
        Buttons.Children.Add(button);
        return button;
    }

    private void RefreshActive()
    {
        foreach (var child in Buttons.Children)
        {
            if (child is Button button && button.Tag is PresetOption preset)
                ApplyPresetStyle(button, preset);
        }
    }

    private void ApplyPresetStyle(Button button, PresetOption preset)
    {
        if (_workspace?.Preset == preset)
        {
            button.Background = (Brush)FindResource("PresetActiveBrush");
            button.Foreground = (Brush)FindResource("WhiteBrush");
            button.BorderBrush = (Brush)FindResource("PresetActiveBrush");
            return;
        }

        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.ForegroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
    }
}
