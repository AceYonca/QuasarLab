using Newtonsoft.Json;
using QuasarCLI.Common.Cryptography;
using QuasarLab.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QuasarLab
{
    /// <summary>
    /// Imports QuasarRecover JSON into a QuasarLab ClientProfile.
    ///
    /// New reports:
    /// - parse the top-level Crypto object directly into QuasarCryptoProfile;
    /// - prefer already-decrypted Settings / QuasarLabCompatibility values.
    ///
    /// Legacy reports:
    /// - if only TagRaw or ServerSignatureRaw exists, decrypt with
    ///   Aes256(encryptionKey, recoveredCryptoProfile).
    ///
    /// Therefore non-default salt/iterations/key lengths/HMAC settings from
    /// QuasarRecover JSON are honored automatically.
    /// </summary>
    public static class QuasarRecoveryProfileImporter
    {
        public static bool TryCreateProfile(
            string json,
            QuasarRecoveryImportDefaults defaults,
            out ClientProfile profile,
            out string message)
        {
            profile = null;
            message = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                message = "Recovery JSON is empty.";
                return false;
            }

            ImportedQuasarRecoveryReport report;

            try
            {
                report =
                    JsonConvert.DeserializeObject<ImportedQuasarRecoveryReport>(
                        json);
            }
            catch (Exception ex)
            {
                message =
                    "Invalid recovery JSON: " +
                    ex.Message;

                return false;
            }

            if (report == null)
            {
                message = "Recovery report is empty.";
                return false;
            }

            ImportedQuasarRecoverySettings settings =
                report.Settings;

            ImportedQuasarLabCompatibility compatibility =
                report.QuasarLabCompatibility;

            if (settings == null &&
                compatibility == null)
            {
                message =
                    "Recovery report does not contain Settings or " +
                    "QuasarLabCompatibility data.";

                return false;
            }

            QuasarCryptoProfile cryptoProfile =
                report.Crypto;

            string host;
            int port;

            if (!TryGetEndpoint(
                    compatibility,
                    settings,
                    out host,
                    out port,
                    out message))
            {
                return false;
            }

            string version =
                FirstNonEmpty(
                    compatibility != null
                        ? compatibility.Version
                        : null,
                    settings != null
                        ? settings.Version
                        : null);

            string encryptionKey =
                FirstNonEmpty(
                    compatibility != null
                        ? compatibility.EncryptionKey
                        : null,
                    settings != null
                        ? settings.EncryptionKey
                        : null,
                    cryptoProfile != null
                        ? cryptoProfile.EncryptionKeyValue
                        : null);

            string tag =
                GetReleaseTag(
                    compatibility,
                    settings,
                    encryptionKey,
                    cryptoProfile);

            byte[] signature;

            if (!TryGetReleaseSignature(
                    compatibility,
                    settings,
                    encryptionKey,
                    cryptoProfile,
                    out signature,
                    out message))
            {
                return false;
            }

            var missing =
                new List<string>();

            if (string.IsNullOrWhiteSpace(host))
                missing.Add("Host");

            if (port < 1 || port > 65535)
                missing.Add("Port");

            if (string.IsNullOrWhiteSpace(version))
                missing.Add("Version");

            if (string.IsNullOrWhiteSpace(encryptionKey))
                missing.Add("EncryptionKey");

            if (signature == null ||
                signature.Length == 0)
            {
                missing.Add("ServerSignature");
            }

            if (missing.Count > 0)
            {
                message =
                    "Recovery report is missing required Release fields: " +
                    string.Join(
                        ", ",
                        missing);

                return false;
            }

            defaults =
                defaults ??
                new QuasarRecoveryImportDefaults();

            profile =
                new ClientProfile
                {
                    Mode = ProfileMode.Release,
                    Name = "Release",

                    Host = host,
                    Port = port,
                    Version = version,

                    OperatingSystem =
                        ValueOrDefault(
                            defaults.OperatingSystem,
                            "Windows 11 Pro x64"),

                    AccountType =
                        ValueOrDefault(
                            defaults.AccountType,
                            "Administrator"),

                    Country =
                        ValueOrDefault(
                            defaults.Country,
                            "United States"),

                    CountryCode =
                        ValueOrDefault(
                            defaults.CountryCode,
                            "US"),

                    ImageIndex =
                        defaults.ImageIndex,

                    PcNameTemplate =
                        ValueOrDefault(
                            defaults.PcNameTemplate,
                            "DESKTOP-{INDEX}"),

                    UsernameTemplate =
                        ValueOrDefault(
                            defaults.UsernameTemplate,
                            "User-{INDEX}"),

                    Tag =
                        tag ?? string.Empty,

                    EncryptionKey =
                        encryptionKey,

                    Signature =
                        signature,

                    CryptoProfile =
                        cryptoProfile
                };

            bool cryptoValidated =
                (cryptoProfile != null &&
                 cryptoProfile.ValidationSucceeded) ||
                (report.RecoveryStatus != null &&
                 report.RecoveryStatus.CryptoValidationSucceeded);

            int confidence =
                cryptoProfile != null
                    ? cryptoProfile.ConfidenceScore
                    : 0;

            if (cryptoValidated)
            {
                message =
                    "Imported validated QuasarRecover profile for " +
                    host +
                    ":" +
                    port +
                    " using recovered crypto parameters (" +
                    confidence +
                    "% confidence).";
            }
            else if (cryptoProfile != null &&
                     cryptoProfile.HasRecoveredParameters)
            {
                message =
                    "Imported QuasarRecover profile for " +
                    host +
                    ":" +
                    port +
                    " with recovered crypto parameters.";
            }
            else
            {
                message =
                    "Imported QuasarRecover profile for " +
                    host +
                    ":" +
                    port +
                    " using stock crypto fallback where needed.";
            }

            return true;
        }

        private static bool TryGetEndpoint(
            ImportedQuasarLabCompatibility compatibility,
            ImportedQuasarRecoverySettings settings,
            out string host,
            out int port,
            out string error)
        {
            host = null;
            port = 4782;
            error = null;

            if (compatibility != null &&
                !string.IsNullOrWhiteSpace(
                    compatibility.Host))
            {
                host =
                    compatibility.Host.Trim();

                if (compatibility.Port >= 1 &&
                    compatibility.Port <= 65535)
                {
                    port =
                        compatibility.Port;
                }

                return true;
            }

            string hosts =
                FirstNonEmpty(
                    compatibility != null
                        ? compatibility.Hosts
                        : null,
                    settings != null
                        ? settings.Hosts
                        : null);

            if (string.IsNullOrWhiteSpace(hosts))
            {
                error =
                    "Recovery report does not contain a recovered Hosts value.";

                return false;
            }

            return TryParseFirstEndpoint(
                hosts,
                out host,
                out port,
                out error);
        }

        private static bool TryParseFirstEndpoint(
            string hosts,
            out string host,
            out int port,
            out string error)
        {
            host = null;
            port = 4782;
            error = null;

            string first =
                hosts
                    .Split(';')
                    .Select(
                        value =>
                            value.Trim())
                    .FirstOrDefault(
                        value =>
                            !string.IsNullOrWhiteSpace(
                                value));

            if (string.IsNullOrWhiteSpace(first))
            {
                error =
                    "Recovered Hosts value does not contain a usable endpoint.";

                return false;
            }

            Uri uri;

            if (Uri.TryCreate(
                    "tcp://" + first,
                    UriKind.Absolute,
                    out uri) &&
                !string.IsNullOrWhiteSpace(
                    uri.Host))
            {
                host =
                    uri.Host;

                if (uri.Port >= 1 &&
                    uri.Port <= 65535)
                {
                    port =
                        uri.Port;
                }

                return true;
            }

            if (!first.Contains(":"))
            {
                host = first;
                return true;
            }

            error =
                "Could not parse recovered endpoint: " +
                first;

            return false;
        }

        private static string GetReleaseTag(
            ImportedQuasarLabCompatibility compatibility,
            ImportedQuasarRecoverySettings settings,
            string encryptionKey,
            QuasarCryptoProfile cryptoProfile)
        {
            string tag =
                FirstNonEmpty(
                    compatibility != null
                        ? compatibility.Tag
                        : null,
                    settings != null
                        ? settings.Tag
                        : null);

            if (!string.IsNullOrWhiteSpace(tag))
                return tag;

            if (settings == null ||
                string.IsNullOrWhiteSpace(
                    settings.TagRaw) ||
                string.IsNullOrWhiteSpace(
                    encryptionKey))
            {
                return string.Empty;
            }

            try
            {
                var aes =
                    new Aes256(
                        encryptionKey,
                        cryptoProfile);

                return aes.Decrypt(
                    settings.TagRaw);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryGetReleaseSignature(
            ImportedQuasarLabCompatibility compatibility,
            ImportedQuasarRecoverySettings settings,
            string encryptionKey,
            QuasarCryptoProfile cryptoProfile,
            out byte[] signature,
            out string error)
        {
            signature = null;
            error = null;

            string decryptedSignature =
                FirstNonEmpty(
                    compatibility != null
                        ? compatibility.ServerSignature
                        : null,
                    settings != null
                        ? settings.ServerSignature
                        : null);

            if (!string.IsNullOrWhiteSpace(
                    decryptedSignature))
            {
                return TryDecodeBase64(
                    decryptedSignature,
                    "ServerSignature",
                    out signature,
                    out error);
            }

            if (settings != null &&
                !string.IsNullOrWhiteSpace(
                    settings.ServerSignatureRaw))
            {
                if (string.IsNullOrWhiteSpace(
                        encryptionKey))
                {
                    error =
                        "ServerSignatureRaw exists, but EncryptionKey is missing.";

                    return false;
                }

                try
                {
                    var aes =
                        new Aes256(
                            encryptionKey,
                            cryptoProfile);

                    string value =
                        aes.Decrypt(
                            settings.ServerSignatureRaw);

                    return TryDecodeBase64(
                        value,
                        "decrypted ServerSignatureRaw",
                        out signature,
                        out error);
                }
                catch (Exception ex)
                {
                    error =
                        "Could not decrypt ServerSignatureRaw with the " +
                        "crypto profile recovered from the JSON report: " +
                        ex.Message;

                    return false;
                }
            }

            error =
                "Recovery report does not contain ServerSignature.";

            return false;
        }

        private static bool TryDecodeBase64(
            string value,
            string fieldName,
            out byte[] bytes,
            out string error)
        {
            bytes = null;
            error = null;

            try
            {
                bytes =
                    Convert.FromBase64String(
                        value.Trim());

                if (bytes.Length == 0)
                {
                    error =
                        fieldName +
                        " decoded to an empty byte array.";

                    return false;
                }

                return true;
            }
            catch (FormatException ex)
            {
                error =
                    fieldName +
                    " is not valid Base64: " +
                    ex.Message;

                return false;
            }
        }

        private static string FirstNonEmpty(
            params string[] values)
        {
            if (values == null)
                return null;

            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return null;
        }

        private static string ValueOrDefault(
            string value,
            string fallback)
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : value;
        }
    }

    public sealed class QuasarRecoveryImportDefaults
    {
        public string OperatingSystem { get; set; }
        public string AccountType { get; set; }
        public string Country { get; set; }
        public string CountryCode { get; set; }
        public int ImageIndex { get; set; }

        public string PcNameTemplate { get; set; }
        public string UsernameTemplate { get; set; }
    }

    public sealed class ImportedQuasarRecoveryReport
    {
        public ImportedQuasarRecoverySettings Settings { get; set; }

        public QuasarCryptoProfile Crypto { get; set; }

        public ImportedQuasarLabCompatibility
            QuasarLabCompatibility
        { get; set; }

        public ImportedQuasarRecoveryStatus
            RecoveryStatus
        { get; set; }
    }

    public sealed class ImportedQuasarRecoverySettings
    {
        public string Version { get; set; }
        public string Hosts { get; set; }

        public string EncryptionKey { get; set; }

        public string Tag { get; set; }
        public string TagRaw { get; set; }

        public string ServerSignature { get; set; }
        public string ServerSignatureRaw { get; set; }
    }

    public sealed class ImportedQuasarLabCompatibility
    {
        public bool IsCompatible { get; set; }
        public string Status { get; set; }

        public string Hosts { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }

        public string Version { get; set; }
        public string Tag { get; set; }
        public string EncryptionKey { get; set; }
        public string ServerSignature { get; set; }

        public List<string> MissingRequiredFields { get; set; }
        public List<string> Notes { get; set; }
    }

    public sealed class ImportedQuasarRecoveryStatus
    {
        public bool CryptoProfileFound { get; set; }
        public int CryptoConfidenceScore { get; set; }

        public string SettingsMode { get; set; }

        public bool CryptoValidationSucceeded { get; set; }
        public int ValidatedCryptoFields { get; set; }
    }
}
