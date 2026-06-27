using ProtoBuf;
using System;
using System.IO;
using QuasarCLI.Protocol;

namespace QuasarCLI.Protocol.Networking
{
    public class PayloadWriter : IDisposable
    {
        private readonly Stream _innerStream;
        private readonly bool _leaveInnerStreamOpen;

        public PayloadWriter(Stream stream, bool leaveInnerStreamOpen)
        {
            if (stream == null)
                throw new ArgumentNullException("stream");

            _innerStream = stream;
            _leaveInnerStreamOpen = leaveInnerStreamOpen;
        }

        public int WriteMessage(IMessage message)
        {
            if (message == null)
                throw new ArgumentNullException("message");

            using (var ms = new MemoryStream(256))
            {
                Serializer.Serialize(ms, message);

                int payloadLength = (int)ms.Length;

                byte[] header = BitConverter.GetBytes(payloadLength);
                _innerStream.Write(header, 0, header.Length);

                ms.Position = 0;
                ms.CopyTo(_innerStream);

                _innerStream.Flush();

                return sizeof(int) + payloadLength;
            }
        }

        public void WriteBytes(byte[] value)
        {
            if (value == null)
                throw new ArgumentNullException("value");

            _innerStream.Write(value, 0, value.Length);
        }

        public void WriteInteger(int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            _innerStream.Write(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            if (_leaveInnerStreamOpen)
            {
                _innerStream.Flush();
            }
            else
            {
                _innerStream.Dispose();
            }
        }
    }
}