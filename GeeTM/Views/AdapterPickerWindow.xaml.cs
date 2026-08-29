using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using GeeTM.Services;

using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace GeeTM.Views;

public partial class AdapterPickerWindow : Window
{
    public event Action<string>? AdapterChosen;

    public AdapterPickerWindow()
    {
        InitializeComponent();

        var names = NetworkMonitorService.GetAvailableAdapterNames();
        foreach (var n in names) AdapterList.Items.Add(n);

        var current = SettingsService.Current.PreferredAdapter;
        if (current == NetworkMonitorService.PreferWifiSentinel)
        {
            WifiOption.IsChecked = true;
        }
        else if (string.IsNullOrEmpty(current))
        {
            AutoOption.IsChecked = true;
        }
        else
        {
            SpecificOption.IsChecked = true;
            AdapterList.SelectedItem = names.FirstOrDefault(n => n == current);
        }
        
        SourceInitialized += (s, e) => ApplyRoundedCorners();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private void ApplyRoundedCorners()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        int preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    private void AdapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterList.SelectedItem != null) SpecificOption.IsChecked = true;
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        string chosen;
        if (WifiOption.IsChecked == true)
        {
            chosen = NetworkMonitorService.PreferWifiSentinel;
        }
        else if (AutoOption.IsChecked == true)
        {
            chosen = "";
        }
        else if (AdapterList.SelectedItem is string named)
        {
            chosen = named;
        }
        else
        {
            MessageBox.Show("Pick an adapter from the list, or choose Wi-Fi or Auto above.",
                "GeeTM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = SettingsService.Current;
        settings.PreferredAdapter = chosen;
        SettingsService.Save(settings);
        AdapterChosen?.Invoke(chosen);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}


