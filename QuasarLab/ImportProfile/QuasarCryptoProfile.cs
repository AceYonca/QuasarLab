using System.Collections.Generic;

namespace QuasarCLI.Common.Cryptography
{
    /// <summary>
    /// Crypto parameters recovered from a Quasar client sample.
    /// Property names intentionally match QuasarRecover's JSON "Crypto" object
    /// so Newtonsoft.Json can deserialize it directly.
    /// </summary>
    public sealed class QuasarCryptoProfile
    {
        public string CryptoTypeName { get; set; }

        public string KdfAlgorithm { get; set; }
        public string KdfHashAlgorithm { get; set; }
        public int? KdfIterations { get; set; }

        public byte[] Salt { get; set; }

        public int? EncryptionKeyLength { get; set; }
        public int? AuthenticationKeyLength { get; set; }

        public int? AesKeySizeBits { get; set; }
        public int? AesBlockSizeBits { get; set; }

        public int? IvLength { get; set; }

        public string CipherMode { get; set; }
        public string PaddingMode { get; set; }

        public string HmacAlgorithm { get; set; }
        public int? HmacLength { get; set; }

        public string EncryptionKeyFieldName { get; set; }
        public string EncryptionKeyValue { get; set; }

        public int ConfidenceScore { get; set; }

        public bool ValidationSucceeded { get; set; }
        public int ValidationAttempts { get; set; }
        public int ValidatedFieldCount { get; set; }

        public List<string> Evidence { get; set; } =
            new List<string>();

        public bool HasRecoveredParameters
        {
            get
            {
                return
                    (Salt != null && Salt.Length > 0) ||
                    KdfIterations.HasValue ||
                    EncryptionKeyLength.HasValue ||
                    AuthenticationKeyLength.HasValue ||
                    IvLength.HasValue ||
                    HmacLength.HasValue ||
                    AesKeySizeBits.HasValue ||
                    AesBlockSizeBits.HasValue ||
                    !string.IsNullOrWhiteSpace(KdfHashAlgorithm) ||
                    !string.IsNullOrWhiteSpace(HmacAlgorithm) ||
                    !string.IsNullOrWhiteSpace(CipherMode) ||
                    !string.IsNullOrWhiteSpace(PaddingMode);
            }
        }
    }
}
