using ProtoBuf;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QuasarCLI.Protocol.Networking
{
    public class PayloadReader
    {
        private const int HeaderSize = 4;
        private const int MaxMessageSize = 1024 * 1024 * 5;

        private readonly Stream _stream;
        private readonly byte[] _headerBuffer = new byte[HeaderSize];

        public PayloadReader(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException("stream");

            _stream = stream;
        }
        public async Task<byte[]> ReadFrameAsync(
    CancellationToken cancellationToken)
        {
            byte[] header = new byte[HeaderSize];

            await ReadExactIntoAsync(
                header,
                0,
                HeaderSize,
                cancellationToken).ConfigureAwait(false);

            int length = BitConverter.ToInt32(header, 0);

            if (length <= 0)
                throw new InvalidDataException("Invalid payload length.");

            if (length > MaxMessageSize)
                throw new InvalidDataException("Payload too large.");

            byte[] payload = new byte[length];

            await ReadExactIntoAsync(
                payload,
                0,
                length,
                cancellationToken).ConfigureAwait(false);

            return payload;
        }

        private async Task ReadExactIntoAsync(
    byte[] buffer,
    int offset,
    int length,
    CancellationToken cancellationToken)
        {
            int total = 0;

            while (total < length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = await _stream.ReadAsync(
                    buffer,
                    offset + total,
                    length - total,
                    cancellationToken).ConfigureAwait(false);

                if (read <= 0)
                    throw new EndOfStreamException("Connection closed.");

                total += read;
            }
        }
        public byte[] ReadFrame()
        {
            ReadExactInto(_headerBuffer, 0, HeaderSize);

            int length = BitConverter.ToInt32(_headerBuffer, 0);

            if (length <= 0)
                throw new InvalidDataException("Invalid payload length.");

            if (length > MaxMessageSize)
                throw new InvalidDataException("Payload too large.");

            byte[] payload = new byte[length];
            ReadExactInto(payload, 0, length);

            return payload;
        }

        public IMessage ReadMessage()
        {
            byte[] payload = ReadFrame();

            using (var ms = new MemoryStream(payload, false))
            {
                return Serializer.Deserialize<IMessage>(ms);
            }
        }

        private void ReadExactInto(byte[] buffer, int offset, int length)
        {
            int total = 0;

            while (total < length)
            {
                int read = _stream.Read(buffer, offset + total, length - total);

                if (read <= 0)
                    throw new EndOfStreamException("Connection closed.");

                total += read;
            }
        }
    }
}