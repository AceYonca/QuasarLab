using QuasarCLI.Common.Messages;
using QuasarCLI.Networking;
using QuasarCLI.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace QuasarLab.Services
{
    public class QuasarLabService
    {
        public class LabConnection
        {
            public int Index { get; set; }
            public string Id { get; set; }
            public string PcName { get; set; }
            public string Username { get; set; }
            public string Status { get; set; }
            public TlsConnection Connection { get; set; }
            public bool ReceiveMonitorStarted { get; set; }
            public bool ManualDisconnectRequested { get; set; }
        }

        public class PacketTraceEntry
        {
            public DateTime Timestamp { get; set; }
            public PacketDirection Direction { get; set; }
            public int ConnectionIndex { get; set; }
            public string ConnectionId { get; set; }
            public string PcName { get; set; }
            public string Username { get; set; }
            public string MessageType { get; set; }
            public int PayloadLength { get; set; }
            public int FrameLength { get; set; }
            public byte[] Payload { get; set; }
            public IMessage Message { get; set; }
            public string Note { get; set; }
        }

        private readonly List<LabConnection> _connections = new List<LabConnection>();
        private readonly object _lock = new object();

        private bool _reconnectLoopRunning;
        private bool _autoReconnect;
        private int _desiredConnections;



        public bool PerformanceMode { get; set; } = true;
        public int UiUpdateEvery { get; set; } = 25;
        public bool EnableReceiveMonitoring { get; set; } = false;

        public event Action ConnectionsChanged;
        public event Action<string> Log;
        public event Action<PacketTraceEntry> PacketCaptured;
        public event Action<string> StatusChanged;

        public bool AutoReconnectEnabled { get; private set; }
        public ClientProfile Profile { get; private set; }

        public IReadOnlyList<LabConnection> Connections
        {
            get
            {
                lock (_lock)
                {
                    return _connections.ToList();
                }
            }
        }

        public void Initialize(ClientProfile profile)
        {
            Profile = profile;

            var packetTypes = TypeRegistry.GetPacketTypes(typeof(IMessage)).ToArray();
            TypeRegistry.AddTypesToSerializer(typeof(IMessage), packetTypes);

            LogMessage("Registered " + packetTypes.Length + " message types.");
            LogMessage("Loaded profile: " + profile.Name);
        }

        public void EnableAutoReconnect()
        {
            _autoReconnect = true;
            AutoReconnectEnabled = true;

            LogMessage("Auto reconnect enabled. Target: " + GetDesiredConnections());
        }

        public void DisableAutoReconnect()
        {
            _autoReconnect = false;
            AutoReconnectEnabled = false;

            LogMessage("Auto reconnect disabled.");
        }

        public void SetReceiveMonitoring(bool enabled)
        {
            EnableReceiveMonitoring = enabled;

            if (!enabled)
                return;

            List<LabConnection> snapshot;

            lock (_lock)
            {
                snapshot = _connections.ToList();
            }

            foreach (var connection in snapshot)
                StartMonitor(connection);
        }

        public int GetDesiredConnections()
        {
            lock (_lock)
            {
                return _desiredConnections;
            }
        }

        public void IncreaseDesiredConnections()
        {
            lock (_lock)
            {
                _desiredConnections++;
            }

            LogMessage("Reconnect target increased to: " + GetDesiredConnections());
        }

        public void SetDesiredConnections(int count)
        {
            lock (_lock)
            {
                _desiredConnections = Math.Max(count, 0);
            }

            LogMessage("Reconnect target set to: " + GetDesiredConnections());
        }

        public async Task StartAutoReconnectAsync(int desiredConnections, int delayMs = 3000)
        {
            _autoReconnect = true;
            AutoReconnectEnabled = true;

            SetDesiredConnections(desiredConnections);

            if (_reconnectLoopRunning)
            {
                LogMessage("Auto reconnect loop already running. Target: " + GetDesiredConnections());
                return;
            }

            _reconnectLoopRunning = true;

            LogMessage("Auto reconnect loop started. Target: " + GetDesiredConnections());

            try
            {
                while (_autoReconnect)
                {
                    MarkDeadConnectionsDisconnected();

                    LogMessage("[RECONNECT] Current: " + GetConnectionCount());
                    LogMessage("[RECONNECT] Target: " + GetDesiredConnections());

                    if (!await CheckServerAsync(1000))
                    {
                        MarkAllConnectionsDisconnected();
                        StatusMessage("Server offline");
                        await Task.Delay(delayMs);
                        continue;
                    }

                    await ReconnectDisconnectedConnectionsAsync();

                    while (_autoReconnect &&
                           GetConnectionCount() < GetDesiredConnections() &&
                           GetConnectionSlotCount() < GetDesiredConnections())
                    {
                        LogMessage("[RECONNECT] Attempting reconnect...");

                        LabConnection result = await ConnectOneInternalAsync();

                        if (result == null)
                        {
                            LogMessage("[RECONNECT] Reconnect failed. Will retry next tick.");
                            break;
                        }

                        LogMessage("[RECONNECT] Reconnected.");
                        RaiseConnectionsChanged();
                        StatusMessage(GetConnectionCount() + " connected");
                    }

                    StatusMessage(GetConnectionCount() + " connected");

                    await Task.Delay(delayMs);
                }
            }
            finally
            {
                _reconnectLoopRunning = false;
                LogMessage("[RECONNECT] Loop stopped.");
            }
        }

        public async Task<LabConnection> ConnectOneAsync()
        {
            LabConnection result = await ConnectOneInternalAsync();

            if (result != null)
                IncreaseDesiredConnections();

            return result;
        }

        public async Task<bool> CheckServerAsync(int timeoutMs = 1500)
        {
            if (Profile == null)
                return false;

            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask =
                        client.ConnectAsync(Profile.Host, Profile.Port);

                    var timeoutTask =
                        Task.Delay(timeoutMs);

                    var finished =
                        await Task.WhenAny(connectTask, timeoutTask);

                    return finished == connectTask &&
                           client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }



        private static string BuildTemplate(string template, string fallback, int index)
        {
            if (string.IsNullOrWhiteSpace(template))
                template = fallback;

            return template
                .Replace("{INDEX}", index.ToString())
                .Replace("{GUID}", Guid.NewGuid().ToString("N").Substring(0, 8));
        }
        private async Task<LabConnection> ConnectOneInternalAsync()
        {
            if (Profile == null)
            {
                LogMessage("No profile loaded.");
                return null;
            }

            int index = GetNextIndex();

            var conn = new TlsConnection();

            if (!PerformanceMode)
            {
                LogMessage("[" + index + "] Connecting to " + Profile.Host + ":" + Profile.Port + "...");
                StatusMessage("Connecting");
            }

            if (!await conn.ConnectAsync(Profile.Host, Profile.Port))
            {
                conn.Dispose();

                if (!PerformanceMode || index % UiUpdateEvery == 0)
                {
                    LogMessage("[" + index + "] TLS connection failed.");
                    StatusMessage("Failed");
                }

                return null;
            }

            string id = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            string pcName = BuildTemplate(Profile.PcNameTemplate, "DESKTOP-{INDEX}", index);
            string username = BuildTemplate(Profile.UsernameTemplate, "User-{INDEX}", index);

            var labConnection = new LabConnection
            {
                Index = index,
                Id = id,
                PcName = pcName,
                Username = username,
                Status = "Connecting",
                Connection = conn
            };

            AttachPacketTrace(labConnection);
            conn.SendMessage(BuildIdentification(labConnection));
            labConnection.Status = "Connected";

            lock (_lock)
            {
                _connections.Add(labConnection);
            }

            if (EnableReceiveMonitoring)
                StartMonitor(labConnection);

            int count = GetConnectionCount();

            if (!PerformanceMode || index % UiUpdateEvery == 0)
            {
                LogMessage(count + " clients connected...");
                RaiseConnectionsChanged();
                StatusMessage(count + " connected");
            }

            return labConnection;
        }

        private ClientIdentification BuildIdentification(LabConnection labConnection)
        {
            var identification = new ClientIdentification
            {
                Version = Profile.Version,
                OperatingSystem = Profile.OperatingSystem,
                AccountType = Profile.AccountType,
                Country = Profile.Country,
                CountryCode = Profile.CountryCode,
                ImageIndex = Profile.ImageIndex,

                Id = labConnection.Id,
                Username = labConnection.Username,
                PcName = labConnection.PcName,

                Tag = Profile.Tag
            };

            if (Profile.Mode == ProfileMode.Release)
            {
                identification.EncryptionKey = Profile.EncryptionKey;
                identification.Signature = Profile.Signature;
            }

            return identification;
        }

        private async Task ReconnectDisconnectedConnectionsAsync()
        {
            List<LabConnection> disconnected;

            lock (_lock)
            {
                disconnected = _connections
                    .Where(c => c.Status == "Disconnected")
                    .Take(Math.Max(GetDesiredConnections() - GetConnectionCount(), 0))
                    .ToList();
            }

            foreach (var labConnection in disconnected)
            {
                if (!_autoReconnect || GetConnectionCount() >= GetDesiredConnections())
                    return;

                LogMessage("[RECONNECT] Reconnecting #" + labConnection.Index + ": " + labConnection.PcName);

                bool reconnected = await ReconnectExistingConnectionAsync(labConnection);

                if (!reconnected)
                {
                    LogMessage("[RECONNECT] Reconnect failed for #" + labConnection.Index + ".");
                    return;
                }

                LogMessage("[RECONNECT] Reconnected #" + labConnection.Index + ".");
                RaiseConnectionsChanged();
                StatusMessage(GetConnectionCount() + " connected");
            }
        }

        private async Task<bool> ReconnectExistingConnectionAsync(LabConnection labConnection)
        {
            if (Profile == null || labConnection == null)
                return false;

            var conn = new TlsConnection();

            if (!await conn.ConnectAsync(Profile.Host, Profile.Port))
            {
                conn.Dispose();
                return false;
            }

            try
            {
                labConnection.Connection = conn;
                labConnection.ManualDisconnectRequested = false;
                labConnection.Status = "Connecting";

                AttachPacketTrace(labConnection);
                conn.SendMessage(BuildIdentification(labConnection));

                labConnection.Status = "Connected";

                if (EnableReceiveMonitoring)
                    StartMonitor(labConnection);

                return true;
            }
            catch
            {
                try
                {
                    conn.Dispose();
                }
                catch
                {
                }

                labConnection.Connection = null;
                labConnection.Status = "Disconnected";

                return false;
            }
        }

        private void StartMonitor(LabConnection labConnection)
        {
            if (labConnection == null ||
                labConnection.Connection == null ||
                labConnection.ReceiveMonitorStarted)
                return;

            labConnection.ReceiveMonitorStarted = true;

            Task.Run(() =>
            {
                bool disconnected = false;

                try
                {
                    while (EnableReceiveMonitoring &&
                           labConnection.Connection != null &&
                           labConnection.Connection.IsConnected)
                    {
                        try
                        {
                            labConnection.Connection.ReadOneMessage();
                        }
                        catch (IOException)
                        {
                            if (labConnection.Connection != null &&
                                labConnection.Connection.IsAlive())
                                continue;

                            disconnected = true;
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            disconnected = true;
                            break;
                        }
                    }
                }
                catch
                {
                    disconnected = true;
                }
                finally
                {
                    labConnection.ReceiveMonitorStarted = false;
                }

                if (!disconnected &&
                    labConnection.Connection != null &&
                    labConnection.Connection.IsAlive())
                    return;

                if (labConnection.ManualDisconnectRequested)
                    return;

                labConnection.Status = "Disconnected";

                LogMessage("[" + labConnection.Index + "] Connection lost: " + labConnection.PcName);
                RaiseConnectionsChanged();
            });
        }

        private void AttachPacketTrace(LabConnection labConnection)
        {
            if (labConnection == null || labConnection.Connection == null)
                return;

            labConnection.Connection.PacketCaptured += (sender, e) =>
            {
                RaisePacketCaptured(labConnection, e);
            };
        }

        private void RaisePacketCaptured(LabConnection labConnection, PacketTraceEventArgs e)
        {
            var handler = PacketCaptured;

            if (handler == null || labConnection == null || e == null)
                return;

            handler(new PacketTraceEntry
            {
                Timestamp = e.Timestamp,
                Direction = e.Direction,
                ConnectionIndex = labConnection.Index,
                ConnectionId = labConnection.Id,
                PcName = labConnection.PcName,
                Username = labConnection.Username,
                MessageType = e.MessageType,
                PayloadLength = e.PayloadLength,
                FrameLength = e.FrameLength,
                Payload = e.Payload,
                Message = e.Message,
                Note = e.Note
            });
        }

        private void MarkDeadConnectionsDisconnected()
        {
            List<LabConnection> deadConnections;

            lock (_lock)
            {
                deadConnections = _connections
                    .Where(c =>
                        c.Status == "Connected" &&
                        (c.Connection == null || !c.Connection.IsAlive()))
                    .ToList();

                foreach (var dead in deadConnections)
                {
                    dead.Status = "Disconnected";
                }
            }

            foreach (var dead in deadConnections)
            {
                try
                {
                    if (dead.Connection != null)
                        dead.Connection.Dispose();
                }
                catch
                {
                }

                dead.Connection = null;
                LogMessage("Marked disconnected: " + dead.PcName);
            }

            if (deadConnections.Count > 0)
                RaiseConnectionsChanged();
        }

        public void MarkAllConnectionsDisconnected()
        {
            List<LabConnection> snapshot;

            lock (_lock)
            {
                snapshot = _connections
                    .Where(c => c.Status != "Disconnected")
                    .ToList();

                foreach (var item in snapshot)
                    item.Status = "Disconnected";
            }

            foreach (var item in snapshot)
            {
                try
                {
                    if (item.Connection != null)
                        item.Connection.Dispose();
                }
                catch
                {
                }

                item.Connection = null;
            }

            if (snapshot.Count > 0)
            {
                RaiseConnectionsChanged();
                StatusMessage("Disconnected");
                LogMessage("Marked clients disconnected.");
            }
        }

        public void DisconnectAll()
        {
            _autoReconnect = false;
            AutoReconnectEnabled = false;
            SetDesiredConnections(0);

            List<LabConnection> snapshot;

            lock (_lock)
            {
                snapshot = _connections.ToList();
                _connections.Clear();
            }

            foreach (var item in snapshot)
            {
                item.ManualDisconnectRequested = true;

                try
                {
                    if (item.Connection != null)
                        item.Connection.Dispose();
                }
                catch
                {
                }

                item.Status = "Disconnected";
            }

            RaiseConnectionsChanged();
            StatusMessage("Disconnected");
            LogMessage("All connections disconnected.");
        }

        public bool DisconnectConnection(int index)
        {
            LabConnection target = null;

            lock (_lock)
            {
                target = _connections.FirstOrDefault(c => c.Index == index);

                if (target == null)
                    return false;

                _connections.Remove(target);
                _desiredConnections = Math.Max(0, Math.Min(_desiredConnections - 1, _connections.Count));
                target.Status = "Disconnected";
                target.ManualDisconnectRequested = true;
            }

            try
            {
                if (target.Connection != null)
                    target.Connection.Dispose();
            }
            catch
            {
            }

            RaiseConnectionsChanged();
            StatusMessage(GetConnectionCount() + " connected");
            LogMessage("Disconnected connection #" + target.Index + ": " + target.PcName);

            return true;
        }

        private int GetConnectionCount()
        {
            lock (_lock)
            {
                return _connections.Count(c => c.Status == "Connected");
            }
        }

        private int GetConnectionSlotCount()
        {
            lock (_lock)
            {
                return _connections.Count;
            }
        }

        private int GetNextIndex()
        {
            lock (_lock)
            {
                if (_connections.Count == 0)
                    return 1;

                return _connections.Max(c => c.Index) + 1;
            }
        }

        private void RaiseConnectionsChanged()
        {
            var handler = ConnectionsChanged;

            if (handler != null)
                handler();
        }

        private void LogMessage(string message)
        {
            var handler = Log;

            if (handler != null)
                handler(message);
        }

        private void StatusMessage(string message)
        {
            var handler = StatusChanged;

            if (handler != null)
                handler(message);
        }
    }
}
