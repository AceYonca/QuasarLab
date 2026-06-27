using Microsoft.Win32;
using Newtonsoft.Json;
using QuasarCLI.Networking;
using QuasarLab.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuasarCLI.Common.Cryptography;
namespace QuasarLab
{
    public partial class MainWindow : Window
    {
        private readonly QuasarLabService _service = new QuasarLabService();
        private readonly Queue<string> _logLines = new Queue<string>();
        private readonly List<PacketLogEntry> _packetEntries = new List<PacketLogEntry>();

        private const int MaxLogLines = 1000;
        private const int MaxPacketEntries = 500;
        private const int ServerMonitorIntervalMs = 3000;
        private const int ServerMonitorTimeoutMs = 800;

        private ClientProfile _profile;
        private int _packetSequence;
        private bool _serverAvailable;
        private bool _serverMonitorRunning;
        private bool _serverStatusKnown;
        private bool _autoReconnectRunning;
        private bool _spawnRunning;
        private bool _shuttingDown;

        public MainWindow()
        {
            InitializeComponent();

            _profile = ClientProfile.Debug();

            _service.Log += AddLog;
            _service.PacketCaptured += AddPacketCapture;
            _service.StatusChanged += SetStatus;
            _service.ConnectionsChanged += RefreshConnectionsList;

            _service.Initialize(_profile);

            LoadProfileIntoUi(_profile);
            RefreshProfileUi();
            RefreshConnectionsList();

            AddLog("Debug profile loaded.");
            MarkServerChecking();
            _ = MonitorServerAsync();
        }

        private void RefreshConnectionsList()
        {
            Dispatcher.Invoke(() =>
            {
                DashboardConnectionsList.Items.Clear();
                ConnectionsManagerList.Items.Clear();

                var connections = _service.Connections.ToList();

                if (connections.Count == 0)
                {
                    AddConnectionListEntry(new ConnectionListEntry
                    {
                        DisplayText = "No active connections",
                        IsConnection = false
                    });

                    return;
                }

                foreach (var c in connections)
                {
                    string item = "#" + c.Index + " | " + c.PcName + " | " + c.Username + " | " + c.Status;

                    AddConnectionListEntry(new ConnectionListEntry
                    {
                        Index = c.Index,
                        DisplayText = item,
                        IsConnection = true
                    });
                }
            });
        }

        private void AddConnectionListEntry(ConnectionListEntry entry)
        {
            DashboardConnectionsList.Items.Add(entry);
            ConnectionsManagerList.Items.Add(entry);
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await EnsureServerReadyAsync("Connect"))
                return;

            var result = await _service.ConnectOneAsync();

            if (result != null)
                AddLog("Reconnect target is now: " + _service.GetDesiredConnections());

            RefreshConnectionsList();
        }

        private async void StartSpawnerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_spawnRunning)
                return;

            if (!await EnsureServerReadyAsync("Spawner"))
                return;

            _spawnRunning = true;

            int amount = ParseInt(ConnectAmountBox.Text, 1);
            int interval = ParseInt(ConnectIntervalBox.Text, 1);
            bool unlimited = UnlimitedConnectCheckBox.IsChecked == true;

            if (interval < 1)
                interval = 1;

            AddLog("Spawner started.");

            int created = 0;
            int refreshEvery = 25;

            try
            {
                while (_spawnRunning)
                {
                    if (!_serverAvailable &&
                        !await EnsureServerReadyAsync("Spawner"))
                        break;

                    var result = await _service.ConnectOneAsync();

                    if (result != null)
                    {
                        created++;
                    }
                    else
                    {
                        bool alive = await _service.CheckServerAsync(ServerMonitorTimeoutMs);
                        ApplyServerAvailability(alive, true);

                        if (!alive)
                            break;
                    }

                    if (created % refreshEvery == 0)
                    {
                        RefreshConnectionsList();
                        SetStatus(_service.Connections.Count + " connected");
                    }

                    if (!unlimited && created >= amount)
                        break;

                    await Task.Delay(interval);
                }
            }
            catch (Exception ex)
            {
                AddLog("Spawner error: " + ex.Message);
            }

            RefreshConnectionsList();

            _spawnRunning = false;
            AddLog("Spawner stopped. Created: " + created);
        }

        private void StopSpawnerButton_Click(object sender, RoutedEventArgs e)
        {
            _spawnRunning = false;
            AddLog("Spawner stop requested.");
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _spawnRunning = false;

            if (AutoReconnectCheckBox.IsChecked == true)
                AutoReconnectCheckBox.IsChecked = false;
            else
                _autoReconnectRunning = false;

            _service.DisconnectAll();

            RefreshConnectionsList();
            AddLog("Disconnected all clients.");
        }

        private void ConnectionList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var listView = sender as ListView;

            if (listView == null)
                return;

            var item = FindParent<ListViewItem>(e.OriginalSource as DependencyObject);

            if (item == null)
            {
                listView.SelectedItem = null;
                return;
            }

            item.IsSelected = true;
            item.Focus();
        }

        private void ConnectionList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var listView = sender as ListView;
            ConnectionListEntry entry;

            if (!TryGetSelectedConnection(listView, out entry))
                e.Handled = true;
        }

        private void DisconnectConnectionMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var contextMenu = menuItem != null ? menuItem.Parent as ContextMenu : null;
            var listView = contextMenu != null ? contextMenu.PlacementTarget as ListView : null;

            ConnectionListEntry entry;

            if (!TryGetSelectedConnection(listView, out entry))
                return;

            if (_service.DisconnectConnection(entry.Index))
                RefreshConnectionsList();
        }

        private static bool TryGetSelectedConnection(ListView listView, out ConnectionListEntry entry)
        {
            entry = listView != null
                ? listView.SelectedItem as ConnectionListEntry
                : null;

            return entry != null && entry.IsConnection;
        }

        private static T FindParent<T>(DependencyObject source) where T : DependencyObject
        {
            while (source != null)
            {
                var typed = source as T;

                if (typed != null)
                    return typed;

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private void LoadProfileButton_Click(object sender, RoutedEventArgs e)
        {
            _profile = ClientProfile.Debug();

            LoadProfileIntoUi(_profile);

            _service.Initialize(_profile);
            RefreshProfileUi();
            MarkServerChecking();

            AddLog("Debug profile loaded.");
        }

        private void ApplyProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int port = ParseInt(ProfilePortBox.Text, 4782);
                int imageIndex = ParseInt(ProfileImageIndexBox.Text, 0);

                string mode = GetComboText(ProfileModeCombo);

                ProfileMode profileMode = mode == "Release"
                    ? ProfileMode.Release
                    : ProfileMode.Debug;

                _profile = new ClientProfile
                {
                    Mode = profileMode,
                    Name = mode,

                    Host = ProfileHostBox.Text,
                    Port = port,

                    Version = ProfileVersionBox.Text,
                    OperatingSystem = ProfileOperatingSystemBox.Text,
                    AccountType = GetComboText(ProfileAccountTypeCombo),
                    Country = ProfileCountryBox.Text,
                    CountryCode = ProfileCountryCodeBox.Text,
                    PcNameTemplate = ProfilePcNameBox.Text,
                    UsernameTemplate = ProfileUsernameBox.Text,

                    ImageIndex = imageIndex,

                    Tag = ProfileTagBox.Text,

                    EncryptionKey = ProfileEncryptionKeyBox.Text,
                    Signature = DecodeBase64(ProfileSignatureBox.Text)
                };

                _service.Initialize(_profile);
                RefreshProfileUi();
                MarkServerChecking();

                AddLog("Applied profile: " + _profile.Name);
            }
            catch (Exception ex)
            {
                AddLog("Apply profile failed: " + ex.Message);
            }
        }

        private void ImportJsonProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            dialog.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*";

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                string json = File.ReadAllText(dialog.FileName);
                var report = JsonConvert.DeserializeObject<QuasarRecoveryReport>(json);

                if (report == null || report.Settings == null)
                {
                    AddLog("Invalid recovery JSON.");
                    return;
                }

                string host;
                int port;

                ParseHost(report.Settings.Hosts, out host, out port);

                _profile = new ClientProfile
                {
                    Mode = ProfileMode.Release,
                    Name = "Release",

                    Host = host,
                    Port = port,

                    Version = report.Settings.Version,
                    OperatingSystem = ProfileOperatingSystemBox.Text,
                    AccountType = GetComboText(ProfileAccountTypeCombo),
                    Country = ProfileCountryBox.Text,
                    CountryCode = ProfileCountryCodeBox.Text,
                    ImageIndex = ParseInt(ProfileImageIndexBox.Text, 0),
                    Tag = GetReleaseTag(report.Settings),
                    EncryptionKey = report.Settings.EncryptionKey,
                    Signature = GetReleaseSignature(report.Settings)
                };

                LoadProfileIntoUi(_profile);

                _service.Initialize(_profile);
                RefreshProfileUi();
                MarkServerChecking();

                AddLog("Imported release profile from JSON.");
            }
            catch (Exception ex)
            {
                AddLog("JSON import failed: " + ex.Message);
            }
        }

        private void ProfileModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProfileModeCombo == null)
                return;

            string mode = GetComboText(ProfileModeCombo);

            if (mode == "Debug")
                AddLog("Debug mode selected.");
            else if (mode == "Release")
                AddLog("Release mode selected. Import JSON or apply Release settings.");
        }

        private void AutoReconnectCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_autoReconnectRunning)
                return;

            _autoReconnectRunning = true;

            int target = GetAutoReconnectTarget();

            _service.SetDesiredConnections(target);

            _ = _service.StartAutoReconnectAsync(target);

            if (target == 0)
            {
                AddLog("Auto reconnect enabled. No existing clients to restore.");
            }
            else
            {
                AddLog(_serverAvailable
                    ? "Auto reconnect enabled. Target: " + target
                    : "Auto reconnect queued. Target: " + target + ". Waiting for server.");
            }
        }

        private void AutoReconnectCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            _autoReconnectRunning = false;
            _service.DisableAutoReconnect();

            AddLog("Auto reconnect disabled.");
        }

        private int GetAutoReconnectTarget()
        {
            int target = _service.GetDesiredConnections();

            if (target > 0)
                return target;

            return _service.Connections.Count;
        }

        private void LoadProfileIntoUi(ClientProfile profile)
        {
            if (profile == null)
                return;

            ProfileHostBox.Text = profile.Host;
            ProfilePortBox.Text = profile.Port.ToString();
            ProfileVersionBox.Text = profile.Version;
            ProfileTagBox.Text = profile.Tag;

            ProfileOperatingSystemBox.Text = profile.OperatingSystem;
            ProfileCountryBox.Text = profile.Country;
            ProfileCountryCodeBox.Text = profile.CountryCode;
            ProfileImageIndexBox.Text = profile.ImageIndex.ToString();
            ProfilePcNameBox.Text = profile.PcNameTemplate;
            ProfileUsernameBox.Text = profile.UsernameTemplate;
            ProfileEncryptionKeyBox.Text = profile.EncryptionKey ?? "";
            ProfileSignatureBox.Text = profile.Signature != null
                ? Convert.ToBase64String(profile.Signature)
                : "";
        }

        private void RefreshProfileUi()
        {
            ProfileModeText.Text = "Mode: " + _profile.Name;
            ProfileHostText.Text = "Host: " + _profile.Host;
            ProfilePortText.Text = "Port: " + _profile.Port;
            ProfileTagText.Text = "Tag: " + _profile.Tag;
        }

        private async Task MonitorServerAsync()
        {
            if (_serverMonitorRunning)
                return;

            _serverMonitorRunning = true;

            try
            {
                while (!_shuttingDown)
                {
                    bool alive = await _service.CheckServerAsync(ServerMonitorTimeoutMs);
                    ApplyServerAvailability(alive, true);

                    await Task.Delay(ServerMonitorIntervalMs);
                }
            }
            finally
            {
                _serverMonitorRunning = false;
            }
        }

        private async Task<bool> EnsureServerReadyAsync(string action)
        {
            bool alive = await _service.CheckServerAsync(ServerMonitorTimeoutMs);
            ApplyServerAvailability(alive, true);

            if (!alive)
            {
                AddLog(action + " blocked: server is offline.");
                return false;
            }

            return true;
        }

        private void MarkServerChecking()
        {
            _serverStatusKnown = false;
            _serverAvailable = false;

            Dispatcher.Invoke(() =>
            {
                SetConnectionActionsEnabled(false);
                StatusText.Text = "Checking server";
                StatusText.Foreground = GetBrush("MutedBrush");
            });
        }

        private void ApplyServerAvailability(bool available, bool logChanges)
        {
            bool changed = !_serverStatusKnown || _serverAvailable != available;

            _serverStatusKnown = true;
            _serverAvailable = available;

            Dispatcher.Invoke(() =>
            {
                SetConnectionActionsEnabled(available);

                if (available)
                {
                    int connected = _service.Connections.Count;
                    StatusText.Text = connected > 0
                        ? connected + " connected"
                        : "Server listening";
                    StatusText.Foreground = GetBrush("SuccessBrush");
                }
                else
                {
                    StatusText.Text = "Server offline";
                    StatusText.Foreground = GetBrush("DangerBrush");

                    _service.MarkAllConnectionsDisconnected();
                    RefreshConnectionsList();

                    if (_spawnRunning)
                        _spawnRunning = false;
                }
            });

            if (logChanges && changed)
            {
                AddLog(available
                    ? "Server monitor: target is online."
                    : "Server monitor: target is offline.");
            }
        }

        private void SetConnectionActionsEnabled(bool enabled)
        {
            DashboardConnectButton.IsEnabled = enabled;
            ConnectionsConnectButton.IsEnabled = enabled;
            StartSpawnerButton.IsEnabled = enabled;
        }

        private Brush GetBrush(string resourceKey)
        {
            return (Brush)FindResource(resourceKey);
        }

        private void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                _logLines.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);

                while (_logLines.Count > MaxLogLines)
                    _logLines.Dequeue();

                RefreshLogViews();
            });
        }

        private void RefreshLogViews()
        {
            string logText = string.Join(Environment.NewLine, _logLines.ToArray());

            LogTextBox.Text = logText;
            LogTextBox.ScrollToEnd();

            if (MessageLogTextBox != null)
            {
                MessageLogTextBox.Text = logText;
                MessageLogTextBox.ScrollToEnd();
            }

            if (MessageLogSummaryText != null)
                MessageLogSummaryText.Text = _logLines.Count + " lines";
        }

        private void SetStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = status;
            });
        }

        private void AddPacketCapture(QuasarLabService.PacketTraceEntry packet)
        {
            if (packet == null)
                return;

            Dispatcher.Invoke(() =>
            {
                var entry = CreatePacketLogEntry(packet);

                _packetEntries.Add(entry);
                PacketListView.Items.Add(entry);

                while (_packetEntries.Count > MaxPacketEntries)
                {
                    _packetEntries.RemoveAt(0);

                    if (PacketListView.Items.Count > 0)
                        PacketListView.Items.RemoveAt(0);
                }

                PacketCaptureSummaryText.Text = _packetEntries.Count + " packets captured";
                PacketListView.ScrollIntoView(entry);

                if (PacketListView.SelectedItem == null)
                    PacketListView.SelectedItem = entry;
            });
        }

        private PacketLogEntry CreatePacketLogEntry(QuasarLabService.PacketTraceEntry packet)
        {
            int sequence = ++_packetSequence;
            string directionText = packet.Direction == PacketDirection.Inbound ? "IN" : "OUT";
            string clientText = "#" + packet.ConnectionIndex + " " + SafeText(packet.PcName);
            string messageType = string.IsNullOrWhiteSpace(packet.MessageType)
                ? "Unknown"
                : packet.MessageType;

            var entry = new PacketLogEntry
            {
                Sequence = sequence,
                SequenceText = sequence.ToString(),
                Timestamp = packet.Timestamp,
                TimeText = packet.Timestamp.ToString("HH:mm:ss.fff"),
                Direction = packet.Direction,
                DirectionText = directionText,
                ClientText = clientText,
                MessageType = messageType,
                PayloadText = FormatByteCount(packet.PayloadLength),
                FrameText = FormatByteCount(packet.FrameLength),
                DetailsText = BuildPacketDetails(sequence, packet, clientText, messageType)
            };

            return entry;
        }

        private static string BuildPacketDetails(
            int sequence,
            QuasarLabService.PacketTraceEntry packet,
            string clientText,
            string messageType)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Packet #" + sequence);
            sb.AppendLine("Time: " + packet.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.AppendLine("Direction: " + packet.Direction);
            sb.AppendLine("Client: " + clientText);
            sb.AppendLine("Username: " + SafeText(packet.Username));
            sb.AppendLine("Connection Id: " + SafeText(packet.ConnectionId));
            sb.AppendLine("Message Type: " + messageType);
            sb.AppendLine("Payload Length: " + packet.PayloadLength + " bytes");
            sb.AppendLine("Frame Length: " + packet.FrameLength + " bytes");
            sb.AppendLine("Note: " + SafeText(packet.Note));
            sb.AppendLine();
            sb.AppendLine("Message");
            sb.AppendLine(FormatMessage(packet.Message));
            sb.AppendLine();
            sb.AppendLine("Payload Hex");
            sb.AppendLine(FormatHex(packet.Payload));

            return sb.ToString();
        }

        private static string FormatMessage(object message)
        {
            if (message == null)
                return "(raw payload)";

            try
            {
                return JsonConvert.SerializeObject(message, Formatting.Indented);
            }
            catch (Exception ex)
            {
                return "(unable to format message: " + ex.Message + ")";
            }
        }

        private static string FormatHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "(empty)";

            const int bytesPerLine = 16;
            const int maxBytes = 4096;

            int length = Math.Min(bytes.Length, maxBytes);
            var sb = new StringBuilder();

            for (int offset = 0; offset < length; offset += bytesPerLine)
            {
                int lineLength = Math.Min(bytesPerLine, length - offset);

                sb.Append(offset.ToString("X4"));
                sb.Append("  ");

                for (int i = 0; i < bytesPerLine; i++)
                {
                    if (i < lineLength)
                        sb.Append(bytes[offset + i].ToString("X2"));
                    else
                        sb.Append("  ");

                    sb.Append(' ');
                }

                sb.Append(' ');

                for (int i = 0; i < lineLength; i++)
                {
                    byte value = bytes[offset + i];
                    sb.Append(value >= 32 && value <= 126 ? (char)value : '.');
                }

                sb.AppendLine();
            }

            if (bytes.Length > maxBytes)
                sb.AppendLine("... truncated " + (bytes.Length - maxBytes) + " bytes ...");

            return sb.ToString();
        }

        private static string FormatByteCount(int bytes)
        {
            if (bytes < 1024)
                return bytes + " B";

            double kb = bytes / 1024.0;

            if (kb < 1024)
                return kb.ToString("0.0") + " KB";

            return (kb / 1024.0).ToString("0.0") + " MB";
        }

        private static string SafeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }
        private async void CheckServerButton_Click(object sender, RoutedEventArgs e)
        {
            AddLog("Checking server...");

            bool alive = await _service.CheckServerAsync(ServerMonitorTimeoutMs);
            ApplyServerAvailability(alive, false);

            if (alive)
            {
                AddLog("Server is listening on " +
                       _profile.Host + ":" +
                       _profile.Port);
            }
            else
            {
                AddLog("Server is offline or not accepting connections.");
            }
        }
        private static string GetComboText(ComboBox combo)
        {
            var item = combo.SelectedItem as ComboBoxItem;

            if (item == null)
                return "";

            var textBlock = item.Content as TextBlock;

            if (textBlock != null)
                return textBlock.Text;

            if (item.Content != null)
                return item.Content.ToString();

            return "";
        }

        private static void ParseHost(string hosts, out string host, out int port)
        {
            host = "127.0.0.1";
            port = 4782;

            if (string.IsNullOrWhiteSpace(hosts))
                return;

            string first = hosts.Split(';')[0];

            if (string.IsNullOrWhiteSpace(first))
                return;

            string[] parts = first.Split(':');

            if (parts.Length >= 1 && !string.IsNullOrWhiteSpace(parts[0]))
                host = parts[0];

            if (parts.Length >= 2)
                int.TryParse(parts[1], out port);
        }

        private static byte[] DecodeBase64(string value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value))
                    return new byte[0];

                return Convert.FromBase64String(value);
            }
            catch
            {
                return new byte[0];
            }
        }

        private static int ParseInt(string value, int fallback)
        {
            int result;

            if (int.TryParse(value, out result))
                return result;

            return fallback;
        }

        private void ClearMessageLogButton_Click(object sender, RoutedEventArgs e)
        {
            _logLines.Clear();
            RefreshLogViews();
        }

        private void CopyMessageLogButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(MessageLogTextBox.Text))
                Clipboard.SetText(MessageLogTextBox.Text);
        }

        private void ClearPacketsButton_Click(object sender, RoutedEventArgs e)
        {
            _packetEntries.Clear();
            PacketListView.Items.Clear();
            PacketDetailsTextBox.Text = "Select a packet to inspect it.";
            PacketCaptureSummaryText.Text = "0 packets captured";
        }

        private void CopyPacketDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(PacketDetailsTextBox.Text))
                Clipboard.SetText(PacketDetailsTextBox.Text);
        }

        private void PacketListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var entry = PacketListView.SelectedItem as PacketLogEntry;

            if (entry == null)
                return;

            PacketDetailsTextBox.Text = entry.DetailsText;
            PacketDetailsTextBox.ScrollToHome();
        }

        private void CaptureInboundCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            _service.SetReceiveMonitoring(true);
            AddLog("Inbound packet capture enabled.");
        }

        private void CaptureInboundCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            _service.SetReceiveMonitoring(false);
            AddLog("Inbound packet capture disabled.");
        }

        private void DashboardTab_Click(object sender, RoutedEventArgs e)
        {
            MainTabs.SelectedIndex = 0;
        }

        private void ConnectionsTab_Click(object sender, RoutedEventArgs e)
        {
            MainTabs.SelectedIndex = 1;
        }

        private void ProfilesTab_Click(object sender, RoutedEventArgs e)
        {
            MainTabs.SelectedIndex = 2;
        }

        private void MessageLogTab_Click(object sender, RoutedEventArgs e)
        {
            MainTabs.SelectedIndex = 3;
        }

        private void PacketInspectorTab_Click(object sender, RoutedEventArgs e)
        {
            MainTabs.SelectedIndex = 4;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            ShutdownService();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            ShutdownService();
            base.OnClosed(e);
        }

        private void ShutdownService()
        {
            if (_shuttingDown)
                return;

            _shuttingDown = true;

            _spawnRunning = false;
            _autoReconnectRunning = false;

            _service.DisconnectAll();
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }


        private static string GetReleaseTag(QuasarRecoverySettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.Tag))
                return settings.Tag;

            if (!string.IsNullOrWhiteSpace(settings.TagRaw))
            {
                var aes = new Aes256(settings.EncryptionKey);
                return aes.Decrypt(settings.TagRaw);
            }

            return "";
        }

        private static byte[] GetReleaseSignature(QuasarRecoverySettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.ServerSignature))
                return Convert.FromBase64String(settings.ServerSignature);

            if (!string.IsNullOrWhiteSpace(settings.ServerSignatureRaw))
            {
                var aes = new Aes256(settings.EncryptionKey);

                string decrypted =
                    aes.Decrypt(settings.ServerSignatureRaw);

                return Convert.FromBase64String(decrypted);
            }

            return Array.Empty<byte>();
        }

        private sealed class PacketLogEntry
        {
            public int Sequence { get; set; }
            public string SequenceText { get; set; }
            public DateTime Timestamp { get; set; }
            public string TimeText { get; set; }
            public PacketDirection Direction { get; set; }
            public string DirectionText { get; set; }
            public string ClientText { get; set; }
            public string MessageType { get; set; }
            public string PayloadText { get; set; }
            public string FrameText { get; set; }
            public string DetailsText { get; set; }
        }

        private sealed class ConnectionListEntry
        {
            public int Index { get; set; }
            public string DisplayText { get; set; }
            public bool IsConnection { get; set; }

            public override string ToString()
            {
                return DisplayText;
            }
        }

    }

    public class QuasarRecoveryReport
    {
        public QuasarRecoverySettings Settings { get; set; }
    }

    public class QuasarRecoverySettings
    {
        public string Version { get; set; }
        public string Hosts { get; set; }

        public int ReconnectDelay { get; set; }

        public string EncryptionKey { get; set; }

        public string Tag { get; set; }
        public string TagRaw { get; set; }

        public string ServerSignature { get; set; }
        public string ServerSignatureRaw { get; set; }

        public string ServerCertificateStrRaw { get; set; }
    }
}
