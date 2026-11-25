using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AlibreAddOnManager;
using AlibreAddOnManager.Models;

namespace AlibreCatalogManager.Catalog
{
    /// <summary>
    /// Packs a build output folder into a .alibreaddon — the same rules as
    /// tools\New-AlibreAddonPackage.ps1: the folder root becomes the package root,
    /// *.pdb and *.alibreaddon files are left out. A folder with no addon.json gets
    /// one built from its .adc first (<see cref="AddOnManifestFactory"/>).
    /// </summary>
    public static class PackageBuilder
    {
        /// <param name="manifestCreated">True if addon.json was missing and has been written into <paramref name="sourceDir"/>.</param>
        public static string Pack(string sourceDir, string outDir, out bool manifestCreated)
        {
            sourceDir = Path.GetFullPath(sourceDir).TrimEnd('\\');
            var manifestPath = Path.Combine(sourceDir, Packages.ManifestEntry);
            manifestCreated = !File.Exists(manifestPath);
            if (manifestCreated) AddOnManifestFactory.WriteFromAdc(sourceDir);

            var manifest = Json.ReadFile<PackageManifest>(manifestPath)
                           ?? throw new InvalidDataException($"{Packages.ManifestEntry} is empty or invalid.");
            var folderName = manifest.FolderName;
            var version = manifest.Version;
            if (!ManagerPaths.IsValidFolderName(folderName) || string.IsNullOrWhiteSpace(version))
                throw new InvalidOperationException($"{Packages.ManifestEntry} needs a plain 'folderName' and a 'version'.");

            Directory.CreateDirectory(outDir);
            var packagePath = Path.Combine(outDir, folderName + "-" + version + Packages.Extension);
            if (File.Exists(packagePath)) File.Delete(packagePath);

            using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    .Where(f => !f.EndsWith(Packages.Extension, StringComparison.OrdinalIgnoreCase));
                foreach (var file in files)
                {
                    var relative = file.Substring(sourceDir.Length + 1).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                }
            }

            return packagePath;
        }
    }
}