namespace AlibreAddOnManager
{
    public class AlibreInstall
    {
        public string Folder { get; set; } = string.Empty;
        public string ExePath { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public int Build { get; set; }

        public override string ToString() => $"Alibre Design {Version}";
    }
}
