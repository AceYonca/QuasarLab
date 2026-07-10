using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QuasarCLI.Common.Cryptography
{
    /// <summary>
    /// Quasar-compatible authenticated AES decryptor/encryptor.
    ///
    /// new Aes256(masterKey) keeps stock Quasar behavior.
    /// new Aes256(masterKey, cryptoProfile) uses parameters recovered
    /// from QuasarRecover JSON and falls back only for missing values.
    /// </summary>
    public sealed class Aes256
    {
        private const int DefaultKeyLength = 32;
        private const int DefaultAuthKeyLength = 64;
        private const int DefaultIvLength = 16;
        private const int DefaultHmacLength = 32;
        private const int DefaultIterations = 50000;
        private const int DefaultAesKeySizeBits = 256;
        private const int DefaultAesBlockSizeBits = 128;

        private static readonly byte[] DefaultSalt =
        {
            0xBF, 0xEB, 0x1E, 0x56, 0xFB, 0xCD, 0x97, 0x3B,
            0xB2, 0x19, 0x02, 0x24, 0x30, 0xA5, 0x78, 0x43,
            0x00, 0x3D, 0x56, 0x44, 0xD2, 0x1E, 0x62, 0xB9,
            0xD4, 0xF1, 0x80, 0xE7, 0xE6, 0xC3, 0x39, 0x41
        };

        private readonly byte[] _key;
        private readonly byte[] _authKey;

        private readonly int _ivLength;
        private readonly int _hmacLength;
        private readonly int _aesKeySizeBits;
        private readonly int _aesBlockSizeBits;

        private readonly CipherMode _cipherMode;
        private readonly PaddingMode _paddingMode;

        private readonly string _hmacAlgorithm;
        private readonly string _kdfHashAlgorithm;

        public Aes256(string masterKey)
            : this(masterKey, null)
        {
        }

        public Aes256(
            string masterKey,
            QuasarCryptoProfile profile)
        {
            if (string.IsNullOrEmpty(masterKey))
            {
                throw new ArgumentException(
                    "Master key cannot be null or empty.",
                    nameof(masterKey));
            }

            byte[] salt =
                profile != null &&
                profile.Salt != null &&
                profile.Salt.Length > 0
                    ? CloneBytes(profile.Salt)
                    : CloneBytes(DefaultSalt);

            int iterations =
                PositiveOrDefault(
                    profile != null
                        ? profile.KdfIterations
                        : null,
                    DefaultIterations);

            int keyLength =
                PositiveOrDefault(
                    profile != null
                        ? profile.EncryptionKeyLength
                        : null,
                    DefaultKeyLength);

            int authKeyLength =
                PositiveOrDefault(
                    profile != null
                        ? profile.AuthenticationKeyLength
                        : null,
                    DefaultAuthKeyLength);

            _ivLength =
                PositiveOrDefault(
                    profile != null
                        ? profile.IvLength
                        : null,
                    DefaultIvLength);

            _hmacAlgorithm =
                ValueOrDefault(
                    profile != null
                        ? profile.HmacAlgorithm
                        : null,
                    "HMACSHA256");

            _hmacLength =
                PositiveOrDefault(
                    profile != null
                        ? profile.HmacLength
                        : null,
                    GetDefaultHmacLength(
                        _hmacAlgorithm));

            _aesKeySizeBits =
                PositiveOrDefault(
                    profile != null
                        ? profile.AesKeySizeBits
                        : null,
                    keyLength * 8);

            _aesBlockSizeBits =
                PositiveOrDefault(
                    profile != null
                        ? profile.AesBlockSizeBits
                        : null,
                    _ivLength * 8);

            if (_aesKeySizeBits <= 0)
                _aesKeySizeBits = DefaultAesKeySizeBits;

            if (_aesBlockSizeBits <= 0)
                _aesBlockSizeBits = DefaultAesBlockSizeBits;

            _cipherMode =
                ParseCipherMode(
                    profile != null
                        ? profile.CipherMode
                        : null);

            _paddingMode =
                ParsePaddingMode(
                    profile != null
                        ? profile.PaddingMode
                        : null);

            _kdfHashAlgorithm =
                ValueOrDefault(
                    profile != null
                        ? profile.KdfHashAlgorithm
                        : null,
                    "SHA1");

            using (Rfc2898DeriveBytes derive =
                CreateDeriver(
                    masterKey,
                    salt,
                    iterations,
                    _kdfHashAlgorithm))
            {
                _key =
                    derive.GetBytes(
                        keyLength);

                _authKey =
                    derive.GetBytes(
                        authKeyLength);
            }
        }

        public string Encrypt(string input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            return Convert.ToBase64String(
                Encrypt(
                    Encoding.UTF8.GetBytes(input)));
        }

        public byte[] Encrypt(byte[] input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            using (var output = new MemoryStream())
            {
                output.Position = _hmacLength;

                using (SymmetricAlgorithm aes =
                    CreateAes())
                {
                    aes.GenerateIV();

                    using (var cryptoStream =
                        new CryptoStream(
                            output,
                            aes.CreateEncryptor(),
                            CryptoStreamMode.Write,
                            true))
                    {
                        output.Write(
                            aes.IV,
                            0,
                            aes.IV.Length);

                        cryptoStream.Write(
                            input,
                            0,
                            input.Length);

                        cryptoStream.FlushFinalBlock();
                    }

                    byte[] data =
                        output.ToArray();

                    byte[] hash;

                    using (HMAC hmac =
                        CreateHmac(
                            _hmacAlgorithm,
                            _authKey))
                    {
                        hash =
                            hmac.ComputeHash(
                                data,
                                _hmacLength,
                                data.Length - _hmacLength);
                    }

                    if (_hmacLength > hash.Length)
                    {
                        throw new CryptographicException(
                            "Configured HMAC length exceeds the selected HMAC output size.");
                    }

                    output.Position = 0;

                    output.Write(
                        hash,
                        0,
                        _hmacLength);
                }

                return output.ToArray();
            }
        }

        public string Decrypt(string input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            return Encoding.UTF8.GetString(
                Decrypt(
                    Convert.FromBase64String(input)));
        }

        public byte[] Decrypt(byte[] input)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            int minimumLength =
                checked(
                    _hmacLength +
                    _ivLength +
                    1);

            if (input.Length < minimumLength)
            {
                throw new CryptographicException(
                    "Encrypted payload is too short for the recovered crypto profile.");
            }

            int authenticatedLength =
                input.Length -
                _hmacLength;

            byte[] computedHash;

            using (HMAC hmac =
                CreateHmac(
                    _hmacAlgorithm,
                    _authKey))
            {
                computedHash =
                    hmac.ComputeHash(
                        input,
                        _hmacLength,
                        authenticatedLength);
            }

            if (_hmacLength > computedHash.Length)
            {
                throw new CryptographicException(
                    "Configured HMAC length exceeds the selected HMAC output size.");
            }

            byte[] receivedHash =
                new byte[_hmacLength];

            Buffer.BlockCopy(
                input,
                0,
                receivedHash,
                0,
                _hmacLength);

            if (!FixedTimeEqualsPrefix(
                    computedHash,
                    receivedHash,
                    _hmacLength))
            {
                throw new CryptographicException(
                    "Invalid message authentication code (MAC).");
            }

            byte[] iv =
                new byte[_ivLength];

            Buffer.BlockCopy(
                input,
                _hmacLength,
                iv,
                0,
                _ivLength);

            int ciphertextOffset =
                _hmacLength +
                _ivLength;

            int ciphertextLength =
                input.Length -
                ciphertextOffset;

            if (ciphertextLength <= 0)
            {
                throw new CryptographicException(
                    "Encrypted payload contains no ciphertext.");
            }

            using (SymmetricAlgorithm aes =
                CreateAes())
            {
                if (iv.Length != aes.BlockSize / 8)
                {
                    throw new CryptographicException(
                        "Recovered IV length does not match the recovered AES block size.");
                }

                aes.IV =
                    iv;

                using (var cipherStream =
                    new MemoryStream(
                        input,
                        ciphertextOffset,
                        ciphertextLength,
                        false))
                using (var cryptoStream =
                    new CryptoStream(
                        cipherStream,
                        aes.CreateDecryptor(),
                        CryptoStreamMode.Read))
                using (var plaintext =
                    new MemoryStream())
                {
                    cryptoStream.CopyTo(
                        plaintext);

                    return plaintext.ToArray();
                }
            }
        }

        private SymmetricAlgorithm CreateAes()
        {
            Aes aes =
                Aes.Create();

            aes.KeySize =
                _aesKeySizeBits;

            aes.BlockSize =
                _aesBlockSizeBits;

            aes.Mode =
                _cipherMode;

            aes.Padding =
                _paddingMode;

            aes.Key =
                CloneBytes(_key);

            return aes;
        }

        private static Rfc2898DeriveBytes CreateDeriver(
            string masterKey,
            byte[] salt,
            int iterations,
            string hashAlgorithm)
        {
            string normalized =
                NormalizeAlgorithmName(
                    hashAlgorithm);

            switch (normalized)
            {
                case "SHA256":
                    return new Rfc2898DeriveBytes(
                        masterKey,
                        salt,
                        iterations,
                        HashAlgorithmName.SHA256);

                case "SHA384":
                    return new Rfc2898DeriveBytes(
                        masterKey,
                        salt,
                        iterations,
                        HashAlgorithmName.SHA384);

                case "SHA512":
                    return new Rfc2898DeriveBytes(
                        masterKey,
                        salt,
                        iterations,
                        HashAlgorithmName.SHA512);

                case "SHA1":
                default:
                    // Matches stock Quasar's legacy constructor behavior.
                    return new Rfc2898DeriveBytes(
                        masterKey,
                        salt,
                        iterations);
            }
        }

        private static HMAC CreateHmac(
            string algorithm,
            byte[] key)
        {
            string normalized =
                NormalizeAlgorithmName(
                    algorithm);

            switch (normalized)
            {
                case "HMACMD5":
                    return new HMACMD5(
                        CloneBytes(key));

                case "HMACSHA1":
                    return new HMACSHA1(
                        CloneBytes(key));

                case "HMACSHA384":
                    return new HMACSHA384(
                        CloneBytes(key));

                case "HMACSHA512":
                    return new HMACSHA512(
                        CloneBytes(key));

                case "HMACSHA256":
                default:
                    return new HMACSHA256(
                        CloneBytes(key));
            }
        }

        private static int GetDefaultHmacLength(
            string algorithm)
        {
            switch (NormalizeAlgorithmName(algorithm))
            {
                case "HMACMD5":
                    return 16;

                case "HMACSHA1":
                    return 20;

                case "HMACSHA384":
                    return 48;

                case "HMACSHA512":
                    return 64;

                case "HMACSHA256":
                default:
                    return DefaultHmacLength;
            }
        }

        private static string NormalizeAlgorithmName(
            string value)
        {
            return
                (value ?? string.Empty)
                    .Replace("-", string.Empty)
                    .Replace("_", string.Empty)
                    .Replace(" ", string.Empty)
                    .Trim()
                    .ToUpperInvariant();
        }

        private static CipherMode ParseCipherMode(
            string value)
        {
            CipherMode parsed;

            if (!string.IsNullOrWhiteSpace(value) &&
                Enum.TryParse(
                    value,
                    true,
                    out parsed))
            {
                return parsed;
            }

            return CipherMode.CBC;
        }

        private static PaddingMode ParsePaddingMode(
            string value)
        {
            PaddingMode parsed;

            if (!string.IsNullOrWhiteSpace(value) &&
                Enum.TryParse(
                    value,
                    true,
                    out parsed))
            {
                return parsed;
            }

            return PaddingMode.PKCS7;
        }

        private static int PositiveOrDefault(
            int? value,
            int fallback)
        {
            return
                value.HasValue &&
                value.Value > 0
                    ? value.Value
                    : fallback;
        }

        private static string ValueOrDefault(
            string value,
            string fallback)
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : value.Trim();
        }

        private static byte[] CloneBytes(
            byte[] value)
        {
            byte[] copy =
                new byte[value.Length];

            Buffer.BlockCopy(
                value,
                0,
                copy,
                0,
                value.Length);

            return copy;
        }

        private static bool FixedTimeEqualsPrefix(
            byte[] computed,
            byte[] received,
            int length)
        {
            if (computed == null ||
                received == null ||
                length < 0 ||
                computed.Length < length ||
                received.Length != length)
            {
                return false;
            }

            int diff = 0;

            for (int i = 0;
                 i < length;
                 i++)
            {
                diff |=
                    computed[i] ^
                    received[i];
            }

            return diff == 0;
        }
    }
}
