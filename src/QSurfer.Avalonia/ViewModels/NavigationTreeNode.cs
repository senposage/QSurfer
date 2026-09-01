using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QSurfer.Avalonia.ViewModels;

public sealed class NavigationTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _childrenLoaded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsPlaceholder { get; init; }
    public bool IsRecoveryFolder { get; init; }
    public bool IsShareRoot { get; init; }
    public Func<NavigationTreeNode, Task>? ExpandAsync { get; init; }
    public ObservableCollection<NavigationTreeNode> Children { get; } = [];
    public string Glyph => IsRecoveryFolder ? "\uE74D" : "\uE8B7";
    public object? IconSource { get; init; }

    public void EnsurePlaceholder()
    {
        if (!IsPlaceholder && !ChildrenLoaded && Children.Count == 0)
        {
            Children.Add(new NavigationTreeNode { IsPlaceholder = true });
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(_isExpanded, value))
            {
                return;
            }

            SetField(ref _isExpanded, value);
            if (!value || ChildrenLoaded)
            {
                return;
            }
            _ = ExpandAsync?.Invoke(this);
        }
    }

    public bool ChildrenLoaded
    {
        get => _childrenLoaded;
        set => SetField(ref _childrenLoaded, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
