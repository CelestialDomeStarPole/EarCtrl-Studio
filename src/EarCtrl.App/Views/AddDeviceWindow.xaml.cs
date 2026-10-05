using System.Windows;
using System.Windows.Input;
using EarCtrl.App.Services;
using EarCtrl.Core.Profiles;
using Wpf.Ui.Controls;

namespace EarCtrl.App.Views;

public partial class AddDeviceWindow : FluentWindow
{
    /// <summary>ListView 行模型。</summary>
    public sealed record ScanItem(string Name, string Model, DeviceProfile Profile, ulong Address);

    public AddDeviceWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        try
        {
            // 只列出「名称命中 Profile 且尚未添加」的已配对设备
            var addable = AgentHost.Instance.AddableProfiles;
            var paired = await Core.Supervision.PairedDeviceScanner.ScanAsync(addable);
            var items = paired
                .Where(d => d.Profile is not null)
                .Select(d => new ScanItem(d.Name, d.Profile!.Model.Name, d.Profile, d.Address))
                .ToList();

            DeviceList.ItemsSource = items;
            ScanningText.Visibility = Visibility.Collapsed;
            NoneText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AddButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            ScanningText.Text = ex.Message;
        }
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        AddButton.IsEnabled = DeviceList.SelectedItem is ScanItem;

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not ScanItem item)
        {
            return;
        }
        AgentHost.Instance.AddDevice(item.Profile);
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
