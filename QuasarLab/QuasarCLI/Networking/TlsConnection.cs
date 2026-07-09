using ProtoBuf;
using QuasarCLI.Protocol;
using QuasarCLI.Protocol.Networking;
using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace QuasarCLI.Networking
{
    public enum PacketDirection
    {
        Inbound,
        Outbound
    }

    public sealed class PacketTraceEventArgs : EventArgs
    {
        public PacketTraceEventArgs(
            PacketDirection direction,
            string messageType,
            int payloadLength,
            int frameLength,
            byte[] payload,
            IMessage message,
            string note)
        {
            Direction = direction;
            MessageType = messageType;
            PayloadLength = payloadLength;
            FrameLength = frameLength;
            Payload = CopyBytes(payload);
            Message = message;
            Note = note;
            Timestamp = DateTime.Now;
        }

        public DateTime Timestamp { get; private set; }
        public PacketDirection Direction { get; private set; }
        public string MessageType { get; private set; }
        public int PayloadLength { get; private set; }
        public int FrameLength { get; private set; }
        public byte[] Payload { get; private set; }
        public IMessage Message { get; private set; }
        public string Note { get; private set; }

        private static byte[] CopyBytes(byte[] value)
        {
            const int MaxCapturedPayloadBytes = 4096;

            if (value == null || value.Length == 0)
                return new byte[0];

            int length =
                Math.Min(
                    value.Length,
                    MaxCapturedPayloadBytes);

            byte[] copy =
                new byte[length];

            Buffer.BlockCopy(
                value,
                0,
                copy,
                0,
                length);

            return copy;
        }
    }

    public sealed class TlsConnection : IDisposable
    {
        private TcpClient _tcpClient;
        private bool _disposed;

        private readonly object _stateLock = new object();
        private int _connectionGeneration;



        private readonly object _writeLock =
    new object();

        private readonly SemaphoreSlim _readLock =
            new SemaphoreSlim(1, 1);
        public SslStream Stream { get; private set; }
        public event EventHandler<PacketTraceEventArgs> PacketCaptured;

        public bool IsConnected
        {
            get
            {
                lock (_stateLock)
                {
                    return !_disposed &&
                           _tcpClient != null &&
                           _tcpClient.Client != null &&
                           _tcpClient.Connected &&
                           Stream != null &&
                           Stream.IsAuthenticated;
                }
            }
        }
        private static void SafeDispose(IDisposable resource)
        {
            if (resource == null)
                return;

            try
            {
                resource.Dispose();
            }
            catch
            {
            }
        }

        private static void SafeClose(TcpClient client)
        {
            if (client == null)
                return;

            try
            {
                client.Close();
            }
            catch
            {
            }
        }
        public async Task<bool> ConnectAsync(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new ArgumentException("Host cannot be empty.", nameof(host));

            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            TcpClient newClient = null;
            SslStream newStream = null;

            TcpClient oldClient = null;
            SslStream oldStream = null;

            int myGeneration;

            try
            {
                lock (_stateLock)
                {
                    // Invalidate any previous or concurrent connection attempt.
                    myGeneration = ++_connectionGeneration;

                    oldClient = _tcpClient;
                    oldStream = Stream;

                    _tcpClient = null;
                    Stream = null;

                    // This TlsConnection object is being reused.
                    _disposed = false;
                }

                // Dispose old resources outside the lock.
                SafeDispose(oldStream);
                SafeClose(oldClient);

                // IMPORTANT:
                // Keep these as local variables until the entire connection
                // and TLS handshake have succeeded.
                newClient = new TcpClient
                {
                    NoDelay = true,
                    ReceiveBufferSize = 4096,
                    SendBufferSize = 4096,
                    LingerState = new LingerOption(true, 0)
                };

                await newClient
                    .ConnectAsync(host, port)
                    .ConfigureAwait(false);

                newStream = new SslStream(
                    newClient.GetStream(),
                    false,
                    ValidateServerCertificate);

                await newStream
                    .AuthenticateAsClientAsync(
                        host,
                        null,
                        SslProtocols.Tls12,
                        false)
                    .ConfigureAwait(false);

                newStream.ReadTimeout = 1500;
                newStream.WriteTimeout = 1500;

                lock (_stateLock)
                {
                    // Dispose() or another ConnectAsync() may have happened
                    // while we were awaiting TCP/TLS operations.
                    if (_disposed ||
                        myGeneration != _connectionGeneration)
                    {
                        return false;
                    }

                    // Connection succeeded. Transfer ownership to the fields.
                    _tcpClient = newClient;
                    Stream = newStream;

                    newClient = null;
                    newStream = null;

                    return true;
                }
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (AuthenticationException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                // These are only non-null when ownership was not transferred
                // to the TlsConnection instance.
                SafeDispose(newStream);
                SafeClose(newClient);
            }
        }

        public void SendRawPayload(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException("payload");

            byte[] frame =
                MessageFramer.BuildFrame(payload);

            lock (_writeLock)
            {
                EnsureConnected();

                Stream.Write(
                    frame,
                    0,
                    frame.Length);

                Stream.Flush();
            }

            RaisePacketCaptured(
                PacketDirection.Outbound,
                "RawPayload",
                payload.Length,
                frame.Length,
                payload,
                null,
                "Sent raw payload");
        }

        public void SendMessage<T>(T message)
         where T : IMessage
        {
            if (message == null)
                throw new ArgumentNullException("message");

            byte[] payload;

            using (var ms = new MemoryStream(256))
            {
                Serializer.Serialize(ms, message);
                payload = ms.ToArray();
            }

            byte[] frame =
                MessageFramer.BuildFrame(payload);

            lock (_writeLock)
            {
                EnsureConnected();

                Stream.Write(
                    frame,
                    0,
                    frame.Length);

                Stream.Flush();
            }

            RaisePacketCaptured(
                PacketDirection.Outbound,
                message.GetType().Name,
                payload.Length,
                frame.Length,
                payload,
                message,
                "Sent message");
        }

        public void SendInvalidLengthNegative()
        {
            SendLengthOnly(-1);
        }

        public void SendOversizedLength()
        {
            SendLengthOnly((1024 * 1024 * 5) + 1);
        }

        public void SendInvalidLengthZero()
        {
            SendLengthOnly(0);
        }

        private void SendLengthOnly(int length)
        {
            byte[] header =
                BitConverter.GetBytes(length);

            lock (_writeLock)
            {
                EnsureConnected();

                Stream.Write(
                    header,
                    0,
                    header.Length);

                Stream.Flush();
            }

            RaisePacketCaptured(
                PacketDirection.Outbound,
                "LengthHeader",
                length,
                header.Length,
                header,
                null,
                "Sent length header only");
        }

        public IMessage ReadOneMessage()
        {
            _readLock.Wait();

            try
            {
                EnsureConnected();

                var reader =
                    new PayloadReader(Stream);

                byte[] payload =
                    reader.ReadFrame();

                IMessage message;

                using (var ms =
                    new MemoryStream(payload, false))
                {
                    message =
                        Serializer.Deserialize<IMessage>(ms);
                }

                RaisePacketCaptured(
                    PacketDirection.Inbound,
                    message != null
                        ? message.GetType().Name
                        : "UnknownMessage",
                    payload.Length,
                    ProtocolConstants.HeaderSize +
                        payload.Length,
                    payload,
                    message,
                    "Received message");

                return message;
            }
            finally
            {
                _readLock.Release();
            }
        }

        public async Task<IMessage> ReadOneMessageAsync(
         CancellationToken cancellationToken)
        {
            await _readLock
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                EnsureConnected();

                var reader =
                    new PayloadReader(Stream);

                byte[] payload =
                    await reader
                        .ReadFrameAsync(cancellationToken)
                        .ConfigureAwait(false);

                IMessage message;

                using (var ms =
                    new MemoryStream(payload, false))
                {
                    message =
                        Serializer.Deserialize<IMessage>(ms);
                }

                RaisePacketCaptured(
                    PacketDirection.Inbound,
                    message != null
                        ? message.GetType().Name
                        : "UnknownMessage",
                    payload.Length,
                    ProtocolConstants.HeaderSize +
                        payload.Length,
                    payload,
                    message,
                    "Received message");

                return message;
            }
            finally
            {
                _readLock.Release();
            }
        }

        public void ReadOneFrame()
        {
            EnsureConnected();

            try
            {
                var reader = new PayloadReader(Stream);
                byte[] payload = reader.ReadFrame();

                RaisePacketCaptured(
                    PacketDirection.Inbound,
                    "RawFrame",
                    payload.Length,
                    ProtocolConstants.HeaderSize + payload.Length,
                    payload,
                    null,
                    "Received raw frame");
            }
            catch
            {
                throw;
            }
        }

        public bool IsAlive()
        {
            try
            {
                if (_disposed)
                    return false;

                if (_tcpClient == null || _tcpClient.Client == null || Stream == null)
                    return false;

                Socket socket = _tcpClient.Client;

                if (!socket.Connected)
                    return false;

                bool readReady = socket.Poll(0, SelectMode.SelectRead);
                bool noData = socket.Available == 0;

                if (readReady && noData)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException("TLS connection is not connected.");
        }

        private static bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors errors)
        {
            return true;
        }

        private void RaisePacketCaptured(
            PacketDirection direction,
            string messageType,
            int payloadLength,
            int frameLength,
            byte[] payload,
            IMessage message,
            string note)
        {
            var handler = PacketCaptured;

            if (handler != null)
            {
                handler(
                    this,
                    new PacketTraceEventArgs(
                        direction,
                        messageType,
                        payloadLength,
                        frameLength,
                        payload,
                        message,
                        note));
            }
        }

        public void Dispose()
        {
            TcpClient clientToDispose;
            SslStream streamToDispose;

            lock (_stateLock)
            {
                if (_disposed)
                    return;

                _disposed = true;

                // Invalidate any ConnectAsync currently awaiting TCP or TLS.
                ++_connectionGeneration;

                clientToDispose = _tcpClient;
                streamToDispose = Stream;

                _tcpClient = null;
                Stream = null;
            }

            SafeDispose(streamToDispose);
            SafeClose(clientToDispose);
        }
    }
}
