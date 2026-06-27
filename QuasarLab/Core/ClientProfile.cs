namespace QuasarLab.Services
{
    public enum ProfileMode
    {
        Debug,
        Release
    }

    public class ClientProfile
    {
        public ProfileMode Mode { get; set; }

        public string Name { get; set; }

        public string Host { get; set; }
        public int Port { get; set; }

        public string Version { get; set; }

        public string OperatingSystem { get; set; }
        public string AccountType { get; set; }
        public string Country { get; set; }
        public string CountryCode { get; set; }
        public int ImageIndex { get; set; }

        public string Tag { get; set; }


        public string PcNameTemplate { get; set; }
        public string UsernameTemplate { get; set; }


        public string EncryptionKey { get; set; }
        public byte[] Signature { get; set; }

        public static ClientProfile Debug()
        {
            return new ClientProfile
            {
                Mode = ProfileMode.Debug,

                Name = "Debug",

                Host = "127.0.0.1",
                Port = 4782,

                Version = "lab",

                OperatingSystem = "Windows 11 Pro x64",
                AccountType = "User",
                Country = "Local Lab",
                CountryCode = "XX",
                ImageIndex = 0,
                PcNameTemplate = "DESKTOP-{INDEX}",
                UsernameTemplate = "User-{INDEX}",
                Tag = "DEBUG",

                EncryptionKey = null,
                Signature = null
            };
        }

        public static ClientProfile Release(
            string host,
            int port,
            string version,
            string tag,
            string encryptionKey,
            byte[] signature)
        {
            return new ClientProfile
            {
                Mode = ProfileMode.Release,

                Name = "Release",

                Host = host,
                Port = port,

                Version = version,

                OperatingSystem = "Windows 11 Pro x64",
                AccountType = "Administrator",
                Country = "United States",

                PcNameTemplate = "DESKTOP-{INDEX}",
                UsernameTemplate = "User-{INDEX}",

                CountryCode = "US",
                ImageIndex = 0,

                Tag = tag,

                EncryptionKey = encryptionKey,
                Signature = signature
            };
        }
    }
}