using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Models;
using ServiceNowDesk.Navigation;

namespace ServiceNowDesk.ViewModels;

public sealed class DeskNavEntry : ObservableObject
{
    private readonly Action<DeskSection> _select;
    private bool _isSelected;

    public DeskNavEntry(DeskNavItem item, bool selected, Action<DeskSection> select)
    {
        Section = item.Section;
        Label = item.Label;
        Icon = item.Icon;
        _isSelected = selected;
        _select = select;
    }

    public DeskSection Section { get; }

    public string Label { get; }

    public string Icon { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!value)
            {
                if (_isSelected)
                    OnPropertyChanged(nameof(IsSelected));
                return;
            }

            if (_isSelected)
                return;

            _isSelected = true;
            OnPropertyChanged(nameof(IsSelected));
            _select(Section);
        }
    }

    public void ShowSelected(bool selected)
    {
        if (_isSelected == selected)
            return;
        _isSelected = selected;
        OnPropertyChanged(nameof(IsSelected));
    }
}
