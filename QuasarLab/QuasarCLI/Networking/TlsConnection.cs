using System;
using System.Net.Security;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using ProtoBuf;
using QuasarCLI.Protocol;
using QuasarCLI.Protocol.Networking;

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
            if (value == null || value.Length == 0)
                return new byte[0];

            byte[] copy = new byte[value.Length];
            Buffer.BlockCopy(value, 0, copy, 0, value.Length);
            return copy;
        }
    }

    public sealed class TlsConnection : IDisposable
    {
        private TcpClient _tcpClient;
        private bool _disposed;

        public SslStream Stream { get; private set; }
        public event EventHandler<PacketTraceEventArgs> PacketCaptured;

        public bool IsConnected
        {
            get
            {
                return !_disposed &&
                       _tcpClient != null &&
                       _tcpClient.Client != null &&
                       _tcpClient.Connected &&
                       Stream != null;
            }
        }

        public async Task<bool> ConnectAsync(string host, int port)
        {
            try
            {
                Dispose();

                _disposed = false;

                _tcpClient = new TcpClient();
                _tcpClient.NoDelay = true;
                _tcpClient.ReceiveBufferSize = 4096;
                _tcpClient.SendBufferSize = 4096;
                _tcpClient.LingerState = new LingerOption(true, 0);

                await _tcpClient.ConnectAsync(host, port);

                Stream = new SslStream(
                    _tcpClient.GetStream(),
                    false,
                    ValidateServerCertificate);

                await Stream.AuthenticateAsClientAsync(
                    host,
                    null,
                    SslProtocols.Tls12,
                    false);

                Stream.ReadTimeout = 1500;
                Stream.WriteTimeout = 1500;

                return true;
            }
            catch
            {
                Dispose();
                return false;
            }
        }

        public void SendRawPayload(byte[] payload)
        {
            EnsureConnected();

            if (payload == null)
                throw new ArgumentNullException("payload");

            byte[] frame = MessageFramer.BuildFrame(payload);

            Stream.Write(frame, 0, frame.Length);
            Stream.Flush();

            RaisePacketCaptured(
                PacketDirection.Outbound,
                "RawPayload",
                payload.Length,
                frame.Length,
                payload,
                null,
                "Sent raw payload");
        }

        public void SendMessage<T>(T message) where T : IMessage
        {
            EnsureConnected();

            if (message == null)
                throw new ArgumentNullException("message");

            byte[] payload;

            using (var ms = new MemoryStream(256))
            {
                Serializer.Serialize(ms, message);
                payload = ms.ToArray();
            }

            byte[] frame = MessageFramer.BuildFrame(payload);

            Stream.Write(frame, 0, frame.Length);
            Stream.Flush();

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
            EnsureConnected();

            byte[] header = BitConverter.GetBytes(length);
            Stream.Write(header, 0, header.Length);
            Stream.Flush();

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
            EnsureConnected();

            try
            {
                var reader = new PayloadReader(Stream);
                byte[] payload = reader.ReadFrame();
                IMessage message;

                using (var ms = new MemoryStream(payload, false))
                {
                    message = Serializer.Deserialize<IMessage>(ms);
                }

                RaisePacketCaptured(
                    PacketDirection.Inbound,
                    message != null ? message.GetType().Name : "UnknownMessage",
                    payload.Length,
                    ProtocolConstants.HeaderSize + payload.Length,
                    payload,
                    message,
                    "Received message");

                return message;
            }
            catch
            {
                throw;
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
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                if (Stream != null)
                    Stream.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_tcpClient != null)
                    _tcpClient.Close();
            }
            catch
            {
            }

            Stream = null;
            _tcpClient = null;
        }
    }
}
