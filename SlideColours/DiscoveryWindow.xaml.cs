using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlideColours.Services;

namespace SlideColours;

public partial class DiscoveryWindow : Window
{
    public string? SelectedIp { get; private set; }

    public DiscoveryWindow(IReadOnlyList<DmxNode> nodes)
    {
        InitializeComponent();
        foreach (var node in nodes)
            NodesList.Items.Add(new ListBoxItem { Content = node.Label, Tag = node.Ip });
        NodesList.SelectedIndex = 0;
    }

    private void Use_Click(object sender, RoutedEventArgs e) => Confirm();

    private void NodesList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

    private void Confirm()
    {
        if (NodesList.SelectedItem is ListBoxItem item)
        {
            SelectedIp = (string)item.Tag;
            DialogResult = true;
        }
    }
}
