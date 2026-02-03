using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LocalDisplayHost.Services;

namespace LocalDisplayHost;

public partial class MainWindow : Window
{
    private const int Port = 8080;
    private DisplayServer? _server;
    private ScreenCapture? _capture;
    private DispatcherTimer? _captureTimer;
    private readonly ObservableCollection<string> _connectedClients = [];
    private int _captureMonitorIndex;

    public MainWindow()
    {
        InitializeComponent();
        ClientsListBox.ItemsSource = _connectedClients;
        DisplayUrlText.Text = $"http://{GetLocalIpAddress()}:{Port}/display";
        PopulateDisplayCombo();
    }

    private void PopulateDisplayCombo()
    {
        var n = ScreenCapture.MonitorCount;
        DisplayComboBox.Items.Clear();
        for (var i = 0; i < n; i++)
            DisplayComboBox.Items.Add(i == 0 ? "Primary" : $"Monitor {i + 1}");
        DisplayComboBox.Items.Add("All (current desktop – all screens combined)");
        DisplayComboBox.SelectedIndex = 0;
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return ip.ToString();
            }
        }
        catch { /* ignore */ }
        return "192.168.1.85"; // fallback
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var n = ScreenCapture.MonitorCount;
            _captureMonitorIndex = DisplayComboBox.SelectedIndex == n ? -1 : DisplayComboBox.SelectedIndex; // last item = All
            _capture = new ScreenCapture { Quality = 75 };
            _server = new DisplayServer(
                Port,
                () => _capture!.CaptureBySelection(_captureMonitorIndex),
                () => ScreenCapture.GetStreamedBounds(_captureMonitorIndex));
            _server.ClientConnected += OnClientConnected;
            _server.ClientDisconnected += OnClientDisconnected;
            _server.Start();

            _captureTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1000 / 30) // 30 FPS
            };
            _captureTimer.Tick += (_, _) =>
            {
                var frame = _capture!.CaptureBySelection(_captureMonitorIndex);
                if (frame != null && frame.Length > 0)
                    _server!.BroadcastFrame(frame);
            };
            _captureTimer.Start();

            StatusText.Text = "Running";
            StatusIndicator.Fill = new SolidColorBrush(Colors.LimeGreen);
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not start server.\n\n{ex.Message}\n\nIf port {Port} is in use, close the other app or choose a different port.",
                "Local Display Host",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _captureTimer?.Stop();
        _captureTimer = null;
        _server?.Stop();
        _server = null;
        _capture = null;
        _connectedClients.Clear();

        StatusText.Text = "Stopped";
        StatusIndicator.Fill = new SolidColorBrush(Colors.Gray);
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
    }

    private void OnClientConnected(string endpoint)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_connectedClients.Contains(endpoint))
                _connectedClients.Add(endpoint);
        });
    }

    private void OnClientDisconnected(string endpoint)
    {
        Dispatcher.Invoke(() => _connectedClients.Remove(endpoint));
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(DisplayUrlText.Text);
        }
        catch { /* ignore */ }
    }
}
