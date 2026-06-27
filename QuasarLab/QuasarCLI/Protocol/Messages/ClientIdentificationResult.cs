using ProtoBuf;
using QuasarCLI.Protocol;

namespace QuasarCLI.Common.Messages
{
    [ProtoContract]
    public class ClientIdentificationResult : IMessage
    {
        [ProtoMember(1)]
        public bool Result { get; set; }
    }
}