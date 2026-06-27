using System;

namespace QuasarCLI.Protocol
{
    public static class MessageFramer
    {
        public static byte[] BuildFrame(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));

            if (payload.Length > ProtocolConstants.MaxMessageSize)
                throw new ArgumentException("Payload too large.");

            int frameLength = ProtocolConstants.HeaderSize + payload.Length;

            byte[] frame = new byte[frameLength];

            int length = payload.Length;

            frame[0] = (byte)length;
            frame[1] = (byte)(length >> 8);
            frame[2] = (byte)(length >> 16);
            frame[3] = (byte)(length >> 24);

            Buffer.BlockCopy(
                payload,
                0,
                frame,
                ProtocolConstants.HeaderSize,
                payload.Length);

            return frame;
        }
    }
}