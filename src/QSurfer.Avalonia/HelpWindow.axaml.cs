using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.Generic;

namespace QSurfer.Avalonia;

public sealed partial class HelpWindow : Window
{
    private readonly Dictionary<string, Control> _topics;

    public HelpWindow()
    {
        InitializeComponent();
        _topics = new Dictionary<string, Control>(StringComparer.Ordinal)
        {
            ["getting-started"] = GettingStartedTopic,
            ["main-controls"] = MainControlsTopic,
            ["search"] = SearchTopic,
            ["filters"] = FiltersTopic,
            ["search-folders"] = SearchFoldersTopic,
            ["results"] = ResultsTopic,
            ["browse"] = BrowseTopic,
            ["navigation"] = NavigationTopic,
            ["file-operations"] = FileOperationsTopic,
            ["favorites"] = FavoritesTopic,
            ["preview"] = PreviewTopic,
            ["recovery"] = RecoveryTopic,
            ["recycle-bin"] = RecycleBinTopic,
            ["version-history"] = VersionHistoryTopic,
            ["settings"] = SettingsTopic,
            ["connection-settings"] = ConnectionSettingsTopic,
            ["behavior-settings"] = BehaviorSettingsTopic,
            ["appearance-settings"] = AppearanceSettingsTopic,
            ["mapping-settings"] = MappingSettingsTopic,
            ["rules-history-settings"] = RulesHistorySettingsTopic,
            ["shortcuts"] = ShortcutsTopic,
            ["troubleshooting"] = TroubleshootingTopic
        };
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void GitHub_Click(object? sender, RoutedEventArgs e)
    {
        var launcher = GetTopLevel(this)?.Launcher;
        if (launcher != null)
        {
            await launcher.LaunchUriAsync(new Uri("https://github.com/senposage/QSurfer"));
        }
    }

    private async void Donate_Click(object? sender, RoutedEventArgs e)
    {
        var launcher = GetTopLevel(this)?.Launcher;
        if (launcher != null)
        {
            await launcher.LaunchUriAsync(new Uri("https://www.paypal.com/paypalme/rjc862003"));
        }
    }

    private void TopicTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TopicTree.SelectedItem is not TreeViewItem { Tag: string topicKey } ||
            !_topics.TryGetValue(topicKey, out var selectedTopic))
        {
            return;
        }

        foreach (var topic in _topics.Values)
        {
            topic.IsVisible = ReferenceEquals(topic, selectedTopic);
        }
    }
}
