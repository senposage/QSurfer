using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using QSurfer.Core.Models;

namespace QSurfer.Avalonia;

public sealed partial class FavoriteGroupsWindow : Window
{
    private FavoriteGroupNode? _selectedGroup;

    public FavoriteGroupsWindow(IEnumerable<string> groups, IEnumerable<string> selectedGroups)
    {
        Groups = new ObservableCollection<FavoriteGroupNode>(BuildTree(groups));
        SelectedGroups = selectedGroups.Where(group => !string.IsNullOrWhiteSpace(group)).ToList();
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<FavoriteGroupNode> Groups { get; }
    public IReadOnlyList<string> SelectedGroups { get; private set; }
    public bool Saved { get; private set; }

    private void GroupTree_SelectionChanged(object? sender, SelectionChangedEventArgs e) => _selectedGroup = GroupTree.SelectedItem as FavoriteGroupNode;

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var name = NewGroupBox.Text?.Trim().Replace('/', '\\').Trim('\\') ?? "";
        var selected = _selectedGroup?.Path ?? "";
        var group = string.IsNullOrWhiteSpace(name)
            ? selected
            : string.IsNullOrWhiteSpace(selected) ? name : selected + "\\" + name;
        SelectedGroups = string.IsNullOrWhiteSpace(group) ? [] : [group];
        Saved = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private static IReadOnlyList<FavoriteGroupNode> BuildTree(IEnumerable<string> groups)
    {
        var roots = new List<FavoriteGroupNode>();
        var nodes = new Dictionary<string, FavoriteGroupNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups
                     .Where(group => !string.IsNullOrWhiteSpace(group))
                     .Select(group => group.Trim().Replace('/', '\\').Trim('\\'))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase))
        {
            var path = "";
            ICollection<FavoriteGroupNode> siblings = roots;
            foreach (var part in group.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                path = string.IsNullOrWhiteSpace(path) ? part : path + "\\" + part;
                if (!nodes.TryGetValue(path, out var node))
                {
                    node = new FavoriteGroupNode { Name = part, Path = path };
                    nodes[path] = node;
                    siblings.Add(node);
                }
                siblings = node.Children;
            }
        }
        return roots;
    }
}
