using QuasarCLI.Common.Messages;
using QuasarCLI.Networking;
using QuasarCLI.Protocol;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuasarLab.Services
{
    public class QuasarLabService
    {
        public bool EnablePacketTracing { get; set; } = true;
        public class LabConnection
        {
            public int Index { get; set; }
            public string Id { get; set; }
            public string PcName { get; set; }
            public string Username { get; set; }
            public string Status { get; set; }

            public TlsConnection Connection { get; set; }

            public bool ManualDisconnectRequested { get; set; }

            public TlsConnection MonitoredConnection { get; set; }

            public CancellationTokenSource MonitorCancellation { get; set; }
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

        private int _nextConnectionIndex;

        public bool PerformanceMode { get; set; } = true;
        public int UiUpdateEvery
        {
            get
            {
                int count;

                lock (_lock)
                {
                    count = _connections.Count;
                }

                if (count >= 5000)
                    return 500;

                if (count >= 1000)
                    return 250;

                if (count >= 100)
                    return 50;

                return 10;
            }
        }
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

            List<LabConnection> snapshot;

            lock (_lock)
            {
                snapshot =
                    _connections.ToList();
            }

            if (!enabled)
            {
                foreach (var connection in snapshot)
                {
                    CancellationTokenSource cts =
                        connection.MonitorCancellation;

                    if (cts == null)
                        continue;

                    try
                    {
                        cts.Cancel();
                    }
                    catch
                    {
                    }
                }

                LogMessage(
                    "Receive monitoring disabled.");

                return;
            }

            foreach (var connection in snapshot)
            {
                if (string.Equals(
                        connection.Status,
                        "Connected",
                        StringComparison.Ordinal))
                {
                    StartMonitor(connection);
                }
            }

            LogMessage(
                "Receive monitoring enabled.");
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

        public async Task StartAutoReconnectAsync(
            int desiredConnections,
            int delayMs = 3000)
        {
            _autoReconnect = true;
            AutoReconnectEnabled = true;

            SetDesiredConnections(desiredConnections);

            if (_reconnectLoopRunning)
            {
                LogMessage(
                    "Auto reconnect loop already running. Target: " +
                    GetDesiredConnections());

                return;
            }

            _reconnectLoopRunning = true;

            LogMessage(
                "Auto reconnect loop started. Target: " +
                GetDesiredConnections());

            try
            {
                while (_autoReconnect)
                {
                    // Only mark connections disconnected when their
                    // actual connection state indicates that they are dead.
                    RefreshDeadConnections();

                    int current = GetConnectionCount();
                    int target = GetDesiredConnections();

                    LogMessage(
                        "[RECONNECT] Current: " +
                        current);

                    LogMessage(
                        "[RECONNECT] Target: " +
                        target);

                    // Nothing needs to be restored.
                    if (current >= target)
                    {
                        StatusMessage(
                            current +
                            " connected");

                        await Task.Delay(delayMs);
                        continue;
                    }

                    // This health check only determines whether we should
                    // attempt NEW connections right now.
                    //
                    // A failure must not destroy existing live connections.
                    bool serverReachable =
                        await CheckServerAsync(
                            1000,
                            false);

                    if (!serverReachable)
                    {
                        StatusMessage(
                            current > 0
                                ? current + " connected | server check failed"
                                : "Server unavailable");

                        await Task.Delay(delayMs);
                        continue;
                    }

                    // First restore existing disconnected slots.
                    await ReconnectDisconnectedConnectionsAsync();

                    // Then create any missing connection slots.
                    while (_autoReconnect &&
                           GetConnectionCount() < GetDesiredConnections() &&
                           GetConnectionSlotCount() < GetDesiredConnections())
                    {
                        LogMessage(
                            "[RECONNECT] Attempting reconnect...");

                        LabConnection result =
                            await ConnectOneInternalAsync();

                        if (result == null)
                        {
                            LogMessage(
                                "[RECONNECT] Reconnect failed. " +
                                "Will retry next tick.");

                            break;
                        }

                        LogMessage(
                            "[RECONNECT] Reconnected.");

                        RaiseConnectionsChanged();

                        StatusMessage(
                            GetConnectionCount() +
                            " connected");
                    }

                    StatusMessage(
                        GetConnectionCount() +
                        " connected");

                    await Task.Delay(delayMs);
                }
            }
            catch (Exception ex)
            {
                LogMessage(
                    "[RECONNECT] Loop error: " +
                    ex.Message);
            }
            finally
            {
                _reconnectLoopRunning = false;

                LogMessage(
                    "[RECONNECT] Loop stopped.");
            }
        }

        public async Task<LabConnection> ConnectOneAsync()
        {
            LabConnection result = await ConnectOneInternalAsync();

            if (result != null)
                IncreaseDesiredConnections();

            return result;
        }

        public async Task<bool> CheckServerAsync(
         int timeoutMs = 1500,
         bool logResult = true)
        {
            if (Profile == null)
                return false;

            TlsConnection connection = null;

            try
            {
                connection = new TlsConnection();

                Task<bool> connectTask =
                    connection.ConnectAsync(
                        Profile.Host,
                        Profile.Port);

                Task timeoutTask =
                    Task.Delay(timeoutMs);

                Task completed =
                    await Task.WhenAny(
                        connectTask,
                        timeoutTask);

                if (completed != connectTask)
                {
                    if (logResult)
                    {
                        LogMessage(
                            "[SERVER CHECK] TLS connection timed out after " +
                            timeoutMs +
                            " ms.");
                    }

                    return false;
                }

                bool connected =
                    await connectTask;

                if (!connected ||
                    !connection.IsConnected)
                {
                    if (logResult)
                    {
                        LogMessage(
                            "[SERVER CHECK] TLS connection failed.");
                    }

                    return false;
                }

                if (logResult)
                {
                    LogMessage(
                        "[SERVER CHECK] TLS server reachable at " +
                        Profile.Host +
                        ":" +
                        Profile.Port +
                        ".");
                }

                return true;
            }
            catch (Exception ex)
            {
                if (logResult)
                {
                    LogMessage(
                        "[SERVER CHECK] Failed: " +
                        ex.Message);
                }

                return false;
            }
            finally
            {
                if (connection != null)
                {
                    try
                    {
                        connection.Dispose();
                    }
                    catch
                    {
                    }
                }
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

            labConnection.Status = "Authenticating";

            conn.SendMessage(BuildIdentification(labConnection));

            bool accepted = await WaitForIdentificationAcceptedAsync(conn);

            if (!accepted)
            {
                labConnection.Status = "Rejected";

                try
                {
                    conn.Dispose();
                }
                catch
                {
                }

                LogMessage(
                    "[" + index + "] Server did not accept client identification.");

                if (!PerformanceMode || index % UiUpdateEvery == 0)
                {
                    RaiseConnectionsChanged();
                    StatusMessage("Authentication failed");
                }

                return null;
            }

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
                LogMessage(
                    "[" + index + "] Authenticated successfully. " +
                    count + " clients connected.");

                RaiseConnectionsChanged();
                StatusMessage(count + " connected");
            }

            return labConnection;
        }
        private async Task<bool> WaitForIdentificationAcceptedAsync(
       TlsConnection conn,
       int timeoutMs = 2500)
        {
            if (conn == null || !conn.IsConnected)
                return false;

            using (var cts =
                new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    IMessage message =
                        await conn
                            .ReadOneMessageAsync(cts.Token)
                            .ConfigureAwait(false);

                    var result =
                        message as ClientIdentificationResult;

                    if (result == null)
                    {
                        LogMessage(
                            "[HANDSHAKE] Expected " +
                            "ClientIdentificationResult but received: " +
                            (message != null
                                ? message.GetType().Name
                                : "null"));

                        return false;
                    }

                    if (!result.Result)
                    {
                        LogMessage(
                            "[HANDSHAKE] Server rejected " +
                            "client identification.");

                        return false;
                    }

                    // Don't generate thousands of success-log entries
                    // during high-volume performance tests.
                    if (!PerformanceMode)
                    {
                        LogMessage(
                            "[HANDSHAKE] Client identification accepted.");
                    }

                    return true;
                }
                catch (OperationCanceledException)
                {
                    LogMessage(
                        "[HANDSHAKE] Timed out after " +
                        timeoutMs +
                        " ms waiting for identification result.");

                    return false;
                }
                catch (IOException ex)
                {
                    LogMessage(
                        "[HANDSHAKE] Connection error: " +
                        ex.Message);

                    return false;
                }
                catch (ObjectDisposedException)
                {
                    LogMessage(
                        "[HANDSHAKE] Connection was closed " +
                        "during authentication.");

                    return false;
                }
                catch (Exception ex)
                {
                    LogMessage(
                        "[HANDSHAKE] Authentication failed: " +
                        ex.Message);

                    return false;
                }
            }
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

            int missing;

            lock (_lock)
            {
                int connected =
                    _connections.Count(c =>
                        string.Equals(
                            c.Status,
                            "Connected",
                            StringComparison.Ordinal));

                missing =
                    Math.Max(
                        _desiredConnections - connected,
                        0);

                disconnected =
                    _connections
                        .Where(c =>
                            string.Equals(
                                c.Status,
                                "Disconnected",
                                StringComparison.Ordinal))
                        .Take(missing)
                        .ToList();
            }

            foreach (var labConnection in disconnected)
            {
                if (!_autoReconnect ||
                    GetConnectionCount() >= GetDesiredConnections())
                {
                    return;
                }

                LogMessage(
                    "[RECONNECT] Reconnecting #" +
                    labConnection.Index +
                    ": " +
                    labConnection.PcName);

                bool reconnected =
                    await ReconnectExistingConnectionAsync(
                        labConnection);

                if (!reconnected)
                {
                    LogMessage(
                        "[RECONNECT] Reconnect failed for #" +
                        labConnection.Index +
                        ".");

                    // Try the remaining disconnected entries
                    // instead of aborting the complete cycle.
                    continue;
                }

                LogMessage(
                    "[RECONNECT] Reconnected #" +
                    labConnection.Index +
                    ".");

                RaiseConnectionsChanged();

                StatusMessage(
                    GetConnectionCount() +
                    " connected");
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
                labConnection.Status = "Authenticating";

                AttachPacketTrace(labConnection);

                conn.SendMessage(BuildIdentification(labConnection));

                bool accepted = await WaitForIdentificationAcceptedAsync(conn);

                if (!accepted)
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

                    LogMessage(
                        "[RECONNECT] Server rejected #" +
                        labConnection.Index + ".");

                    return false;
                }

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
        private void StartMonitor(
          LabConnection labConnection)
        {
            if (labConnection == null ||
                labConnection.Connection == null ||
                !EnableReceiveMonitoring)
            {
                return;
            }

            TlsConnection monitoredConnection =
                labConnection.Connection;

            // If this exact connection already has a live monitor,
            // do not create another reader.
            if (ReferenceEquals(
                    labConnection.MonitoredConnection,
                    monitoredConnection) &&
                labConnection.MonitorCancellation != null &&
                !labConnection.MonitorCancellation
                    .IsCancellationRequested)
            {
                return;
            }

            // Stop any previous monitor associated with this slot.
            CancellationTokenSource previousCts =
                labConnection.MonitorCancellation;

            if (previousCts != null)
            {
                try
                {
                    previousCts.Cancel();
                }
                catch
                {
                }
            }

            var monitorCts =
                new CancellationTokenSource();

            CancellationToken token =
                monitorCts.Token;

            labConnection.MonitoredConnection =
                monitoredConnection;

            labConnection.MonitorCancellation =
                monitorCts;

            Task.Run(async () =>
            {
                bool disconnected = false;

                try
                {
                    while (EnableReceiveMonitoring &&
                           !token.IsCancellationRequested &&
                           monitoredConnection.IsConnected)
                    {
                        // This connection slot may have been reconnected
                        // and assigned a completely new TlsConnection.
                        if (!ReferenceEquals(
                                labConnection.Connection,
                                monitoredConnection))
                        {
                            return;
                        }

                        try
                        {
                            await monitoredConnection
                                .ReadOneMessageAsync(token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        catch (IOException)
                        {
                            if (monitoredConnection.IsAlive())
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
                    // Only clear monitor state if this is still the
                    // monitor associated with this exact connection
                    // and this exact CancellationTokenSource.
                    if (ReferenceEquals(
                            labConnection.MonitoredConnection,
                            monitoredConnection) &&
                        ReferenceEquals(
                            labConnection.MonitorCancellation,
                            monitorCts))
                    {
                        labConnection.MonitoredConnection =
                            null;

                        labConnection.MonitorCancellation =
                            null;
                    }

                    try
                    {
                        monitorCts.Dispose();
                    }
                    catch
                    {
                    }
                }

                if (token.IsCancellationRequested)
                    return;

                if (!ReferenceEquals(
                        labConnection.Connection,
                        monitoredConnection))
                {
                    return;
                }

                if (!disconnected &&
                    monitoredConnection.IsAlive())
                {
                    return;
                }

                if (labConnection.ManualDisconnectRequested)
                    return;

                labConnection.Status =
                    "Disconnected";

                LogMessage(
                    "[" +
                    labConnection.Index +
                    "] Connection lost: " +
                    labConnection.PcName);

                RaiseConnectionsChanged();
            });
        }

        private void AttachPacketTrace(
         LabConnection labConnection)
        {
            if (!EnablePacketTracing)
                return;

            if (labConnection == null ||
                labConnection.Connection == null)
            {
                return;
            }

            labConnection.Connection.PacketCaptured +=
                (sender, e) =>
                {
                    if (!EnablePacketTracing)
                        return;

                    RaisePacketCaptured(
                        labConnection,
                        e);
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

        public int RefreshDeadConnections()
        {
            var deadConnections =
                new List<Tuple<
                    LabConnection,
                    TlsConnection,
                    CancellationTokenSource>>();

            lock (_lock)
            {
                foreach (var connection in _connections)
                {
                    if (!string.Equals(
                            connection.Status,
                            "Connected",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    TlsConnection transport =
                        connection.Connection;

                    bool isDead =
                        transport == null ||
                        !transport.IsAlive();

                    if (!isDead)
                        continue;

                    CancellationTokenSource monitorCts =
                        connection.MonitorCancellation;

                    connection.Status = "Disconnected";
                    connection.Connection = null;
                    connection.MonitoredConnection = null;
                    connection.MonitorCancellation = null;

                    deadConnections.Add(
                        Tuple.Create(
                            connection,
                            transport,
                            monitorCts));
                }
            }

            foreach (var item in deadConnections)
            {
                LabConnection labConnection =
                    item.Item1;

                TlsConnection transport =
                    item.Item2;

                CancellationTokenSource monitorCts =
                    item.Item3;

                if (monitorCts != null)
                {
                    try
                    {
                        monitorCts.Cancel();
                    }
                    catch
                    {
                    }
                }

                try
                {
                    if (transport != null)
                        transport.Dispose();
                }
                catch
                {
                }

                LogMessage(
                    "[" +
                    labConnection.Index +
                    "] Marked disconnected: " +
                    labConnection.PcName);
            }

            if (deadConnections.Count > 0)
            {
                RaiseConnectionsChanged();

                StatusMessage(
                    GetConnectionCount() +
                    " connected");
            }

            return deadConnections.Count;
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
            return Interlocked.Increment(ref _nextConnectionIndex);
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
