using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AlibreAddOnManager;
using AlibreAddOnManager.Models;

namespace AlibreCatalogManager.Catalog
{
    /// <summary>
    /// An open, local catalog folder (design doc §2): index.json, addons/{GUID}.json
    /// and the packages they reference. All edits are held in memory — package
    /// copies and file deletions included — and nothing touches disk until
    /// <see cref="Save"/>, which writes only what changed.
    /// </summary>
    public sealed class CatalogDocument
    {
        public const string IndexFileName = "index.json";
        public const string AddOnsFolder = "addons";
        public const string PackagesFolder = "packages";

        private CatalogIndex _index = null!;

        /// <summary>Package files to copy into the catalog on save: destination → source.</summary>
        private readonly Dictionary<string, string> _pendingCopies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Catalog files to delete on save.</summary>
        private readonly HashSet<string> _pendingDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private CatalogDocument()
        {
        }

        public string RootFolder { get; private set; } = null!;

        public string IndexPath { get; private set; } = null!;

        public List<CatalogItem> Items { get; } = new List<CatalogItem>();

        /// <summary>Problems found while loading (unreadable manifests and the like).</summary>
        public List<CatalogIssue> LoadIssues { get; } = new List<CatalogIssue>();

        public bool IndexDirty { get; private set; }

        public bool HasUnsavedChanges =>
            IndexDirty || _pendingCopies.Count > 0 || _pendingDeletes.Count > 0 || Items.Any(i => i.ManifestDirty);

        public int SchemaVersion => _index.SchemaVersion;

        #region Load

        /// <summary>Creates an empty catalog in <paramref name="folder"/> (written on first save).</summary>
        public static CatalogDocument CreateNew(string folder)
        {
            var root = Path.GetFullPath(folder);
            if (File.Exists(Path.Combine(root, IndexFileName)))
                throw new InvalidOperationException(root + " already contains a catalog; open it instead.");
            return new CatalogDocument
            {
                RootFolder = root,
                IndexPath = Path.Combine(root, IndexFileName),
                _index = new CatalogIndex { SchemaVersion = 1 },
                IndexDirty = true
            };
        }

        /// <summary>Opens a catalog folder, or the folder of an index.json.</summary>
        public static CatalogDocument Open(string folderOrIndex)
        {
            var path = Path.GetFullPath(folderOrIndex);
            if (Directory.Exists(path)) path = Path.Combine(path, IndexFileName);
            if (!File.Exists(path)) throw new FileNotFoundException("No " + IndexFileName + " found.", path);

            var root = Path.GetDirectoryName(path)
                       ?? throw new InvalidDataException("The catalog index has no containing folder.");
            var index = Json.ReadFile<CatalogIndex>(path)
                        ?? throw new InvalidDataException("The catalog index is empty or invalid.");
            var doc = new CatalogDocument
            {
                IndexPath = path,
                RootFolder = root,
                _index = index
            };
            if (doc._index.AddOns == null) doc._index.AddOns = new List<CatalogEntry>();
            doc.LoadItems();
            return doc;
        }

        private void LoadItems()
        {
            var seenManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = _index.AddOns ?? new List<CatalogEntry>();
            _index.AddOns = entries;
            foreach (var entry in entries.Where(e => e != null))
            {
                var item = new CatalogItem { Id = ManagerPaths.NormalizeId(entry.Id) ?? string.Empty, Entry = entry, Listed = true };
                item.ManifestPath = ToLocalPath(entry.ManifestUrl);
                if (item.ManifestPath == null)
                    item.ManifestError = string.IsNullOrWhiteSpace(entry.ManifestUrl) ? "No manifestUrl." : "Remote manifest — not editable here.";
                else
                {
                    seenManifests.Add(item.ManifestPath);
                    LoadManifest(item);
                }

                Items.Add(item);
            }

            // Manifests with no index entry: unlisted add-ons that can be re-listed.
            var addOnsDir = Path.Combine(RootFolder, AddOnsFolder);
            if (!Directory.Exists(addOnsDir)) return;
            foreach (var file in Directory.GetFiles(addOnsDir, "*.json").Where(f => !seenManifests.Contains(Path.GetFullPath(f))))
            {
                var item = new CatalogItem { ManifestPath = Path.GetFullPath(file) };
                LoadManifest(item);
                if (item.Manifest == null)
                {
                    LoadIssues.Add(CatalogIssue.Warning(null, $"{AddOnsFolder}/{Path.GetFileName(file)} could not be read: {item.ManifestError}"));
                    continue;
                }

                item.Id = ManagerPaths.NormalizeId(item.Manifest.Id) ?? string.Empty;
                item.Entry = NewEntry(item.Manifest, RelativeUrl(item.ManifestPath));
                Items.Add(item);
            }
        }

        private static void LoadManifest(CatalogItem item)
        {
            var manifestPath = item.ManifestPath;
            if (manifestPath == null)
            {
                item.ManifestError = "No manifest path.";
                return;
            }

            if (!File.Exists(manifestPath))
            {
                item.ManifestError = "Manifest file not found.";
                return;
            }

            try
            {
                var manifest = Json.ReadFile<AddOnManifest>(manifestPath)
                               ?? throw new InvalidDataException("The manifest is empty or invalid.");
                if (manifest.Versions == null) manifest.Versions = new List<AddOnVersion>();
                item.Manifest = manifest;
                if (string.IsNullOrEmpty(item.Id)) item.Id = ManagerPaths.NormalizeId(manifest.Id) ?? string.Empty;
            }
            catch (Exception e)
            {
                item.ManifestError = e.Message;
            }
        }

        #endregion

        #region Paths

        /// <summary>
        /// Full local path of a catalog URL, resolved like the manager does — relative
        /// to index.json. Null for http(s) URLs or anything that is not a path.
        /// </summary>
        public string? ToLocalPath(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                var uri = new Uri(new Uri(IndexPath), url);
                return uri.IsFile ? Path.GetFullPath(uri.LocalPath) : null;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        /// <summary>Forward-slash URL of a catalog file, relative to index.json.</summary>
        public string RelativeUrl(string fullPath)
        {
            var root = RootFolder.TrimEnd('\\') + "\\";
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(fullPath + " is outside the catalog folder.");
            return fullPath.Substring(root.Length).Replace('\\', '/');
        }

        public bool IsInsideCatalog(string? fullPath) => fullPath != null && ManagerPaths.IsSameOrUnder(fullPath, RootFolder);

        /// <summary>
        /// The package file to read for a version: the pending source if it has not
        /// been copied in yet, else the local catalog file. Null for remote packages.
        /// </summary>
        public string? PackageFileFor(AddOnVersion? version)
        {
            var local = ToLocalPath(version?.PackageUrl);
            if (local == null) return null;
            return _pendingCopies.TryGetValue(local, out var source) ? source : local;
        }

        #endregion

        #region Add / update

        /// <summary>
        /// Adds a new add-on from a package. The package must be valid and its id new
        /// to the catalog; it is copied to packages/ on save.
        /// </summary>
        public CatalogItem AddAddOn(string packagePath, PackageValidationResult validation)
        {
            var package = validation.Manifest
                          ?? throw new InvalidOperationException("Package validation did not provide a manifest.");
            var id = ManagerPaths.NormalizeId(package.Id)
                     ?? throw new InvalidOperationException("The package manifest has no valid id.");
            var folderName = package.FolderName;
            if (string.IsNullOrWhiteSpace(folderName))
                throw new InvalidOperationException("The package manifest has no folder name.");
            if (Find(id) != null) throw new InvalidOperationException($"{package.Name} ({id}) is already in the catalog; add a version instead.");

            var folderOwner = Items.FirstOrDefault(i => string.Equals(i.Manifest?.FolderName, package.FolderName, StringComparison.OrdinalIgnoreCase));
            if (folderOwner != null)
                throw new InvalidOperationException($"Folder name '{package.FolderName}' is already used by {folderOwner.Name}; one add-on would overwrite the other.");

            var adc = validation.Adc;
            var manifest = new AddOnManifest
            {
                SchemaVersion = 1,
                Id = id,
                Name = FirstNonEmpty(package.Name, adc?.DisplayName),
                FolderName = folderName,
                Publisher = new Publisher { Name = FirstNonEmpty(package.Publisher, adc?.AuthorName), Url = NullIfEmpty(adc?.AuthorLink) },
                Homepage = NullIfEmpty(adc?.AuthorLink),
                Description = FirstNonEmpty(package.Description, adc?.Description)
            };

            var manifestPath = Path.Combine(RootFolder, AddOnsFolder, id + ".json");
            var item = new CatalogItem
            {
                Id = id,
                Manifest = manifest,
                ManifestPath = manifestPath,
                Entry = NewEntry(manifest, RelativeUrl(manifestPath)),
                Listed = true,
                ManifestDirty = true
            };
            var workspace = adc?.Workspace;
            if (workspace != null && !string.IsNullOrWhiteSpace(workspace)) item.Entry.Workspaces = new List<string> { workspace.Trim() };

            _pendingDeletes.Remove(manifestPath);
            Items.Add(item);
            AddVersion(item, packagePath, validation);
            IndexDirty = true;
            return item;
        }

        /// <summary>Adds a new version of an existing add-on from a validated package.</summary>
        public AddOnVersion AddVersion(CatalogItem item, string packagePath, PackageValidationResult validation)
        {
            RequireEditable(item);
            var package = validation.Manifest
                          ?? throw new InvalidOperationException("Package validation did not provide a manifest.");
            var manifest = item.Manifest
                           ?? throw new InvalidOperationException("The catalog manifest is not editable.");
            var packageId = ManagerPaths.NormalizeId(package.Id)
                            ?? throw new InvalidOperationException("The package manifest has no valid id.");
            var packageVersion = package.Version;
            if (string.IsNullOrWhiteSpace(packageVersion))
                throw new InvalidOperationException("The package manifest has no version.");
            var packageFolder = package.FolderName;
            if (string.IsNullOrWhiteSpace(packageFolder))
                throw new InvalidOperationException("The package manifest has no folder name.");
            if (!ManagerPaths.SameId(packageId, item.Id))
                throw new InvalidOperationException($"The package is {package.Id}, not {item.Id}.");
            if ((manifest.Versions ?? new List<AddOnVersion>()).Any(v => v != null && Packages.CompareVersions(v.Version, packageVersion) == 0))
                throw new InvalidOperationException($"Version {packageVersion} is already in the catalog; remove it first to replace it.");

            var destination = Path.Combine(RootFolder, PackagesFolder, packageFolder + "-" + packageVersion + Packages.Extension);
            var source = Path.GetFullPath(packagePath);
            var sha = Packages.Sha256File(source);

            if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(destination) && !_pendingDeletes.Contains(destination) && Packages.Sha256File(destination) != sha)
                    throw new InvalidOperationException($"{RelativeUrl(destination)} already exists with different content.");
                if (!File.Exists(destination) || _pendingDeletes.Contains(destination))
                    _pendingCopies[destination] = source;
            }

            _pendingDeletes.Remove(destination);

            var version = new AddOnVersion
            {
                Version = packageVersion,
                Released = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Kind = "integrated",
                DllType = NullIfEmpty(package.DllType) ?? (validation.Adc != null ? AdcFile.DllTypeName(validation.Adc.DllType) : null),
                Arch = NullIfEmpty(package.Arch),
                MinAlibreBuild = package.MinAlibreBuild,
                MaxAlibreBuild = package.MaxAlibreBuild,
                PackageUrl = RelativeUrl(destination),
                Sha256 = sha,
                Size = new FileInfo(source).Length
            };
            manifest.Versions = manifest.Versions ?? new List<AddOnVersion>();
            manifest.Versions.Add(version);
            item.ManifestDirty = true;
            SyncLatestVersion(item);
            return version;
        }

        /// <summary>Recomputes sha256 and size after a package was rebuilt in place.</summary>
        public void Rehash(CatalogItem item, AddOnVersion version)
        {
            RequireEditable(item);
            var file = PackageFileFor(version);
            if (file == null || !File.Exists(file)) throw new InvalidOperationException("The package is not a local file in this catalog.");
            version.Sha256 = Packages.Sha256File(file);
            version.Size = new FileInfo(file).Length;
            item.ManifestDirty = true;
        }

        public void MarkIndexDirty() => IndexDirty = true;

        public void MarkManifestDirty(CatalogItem item)
        {
            RequireEditable(item);
            item.ManifestDirty = true;
        }

        /// <summary>Keeps index.json's latestVersion (and name) in step with the manifest.</summary>
        public void SyncLatestVersion(CatalogItem item)
        {
            var latest = item.LatestVersion?.Version;
            if (item.Entry != null && item.Entry.LatestVersion != latest)
            {
                item.Entry.LatestVersion = latest;
                if (item.Listed) IndexDirty = true;
            }
        }

        #endregion

        #region Remove / delete

        /// <summary>Removes the add-on from index.json; its manifest and packages stay, so it can be re-listed.</summary>
        public void Unlist(CatalogItem item)
        {
            if (!item.Listed) return;
            item.Listed = false;
            IndexDirty = true;
        }

        public void Relist(CatalogItem item)
        {
            if (item.Listed) return;
            if (Items.Any(i => i != item && i.Listed && i.Id == item.Id))
                throw new InvalidOperationException("Another listed entry already has id " + item.Id + ".");
            item.Listed = true;
            SyncLatestVersion(item);
            IndexDirty = true;
        }

        /// <summary>Package files only this version uses, which can be deleted with it.</summary>
        public List<string> DeletablePackages(CatalogItem item, IEnumerable<AddOnVersion> versions)
        {
            var removing = new HashSet<AddOnVersion>(versions);
            var stillUsed = new HashSet<string>(
                Items.Select(i => i.Manifest)
                    .OfType<AddOnManifest>()
                    .SelectMany(m => m.Versions ?? new List<AddOnVersion>())
                    .Where(v => v != null && !(removing.Contains(v)))
                    .Select(v => ToLocalPath(v.PackageUrl)).OfType<string>(),
                StringComparer.OrdinalIgnoreCase);

            return removing.Select(v => ToLocalPath(v.PackageUrl))
                .OfType<string>()
                .Where(p => IsInsideCatalog(p) && !stillUsed.Contains(p) && (File.Exists(p) || _pendingCopies.ContainsKey(p)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Removes one version. The last version cannot be removed — delete the add-on instead.</summary>
        public void RemoveVersion(CatalogItem item, AddOnVersion version, bool deletePackage)
        {
            RequireEditable(item);
            var manifest = item.Manifest
                           ?? throw new InvalidOperationException("The catalog manifest is not editable.");
            if ((manifest.Versions ?? new List<AddOnVersion>()).Count(v => v != null) <= 1)
                throw new InvalidOperationException("This is the add-on's only version. Delete the add-on instead.");

            var packages = deletePackage ? DeletablePackages(item, new[] { version }) : new List<string>();
            manifest.Versions = manifest.Versions ?? new List<AddOnVersion>();
            manifest.Versions.Remove(version);
            item.ManifestDirty = true;
            foreach (var p in packages) DeleteOnSave(p);
            SyncLatestVersion(item);
        }

        /// <summary>
        /// Deletes the add-on from the catalog: index entry, manifest, a local icon
        /// and, optionally, the package files no other entry uses.
        /// </summary>
        public void Delete(CatalogItem item, bool deletePackages)
        {
            var packages = deletePackages && item.Manifest != null
                ? DeletablePackages(item, (item.Manifest.Versions ?? new List<AddOnVersion>()).Where(v => v != null))
                : new List<string>();

            Items.Remove(item);
            if (item.Listed) IndexDirty = true;

            // Another entry may still point at the same manifest (a duplicate listing).
            var manifestPath = item.ManifestPath;
            if (manifestPath != null && IsInsideCatalog(manifestPath) && !Items.Any(i => string.Equals(i.ManifestPath, manifestPath, StringComparison.OrdinalIgnoreCase)))
                DeleteOnSave(manifestPath);

            var icon = ToLocalPath(item.Entry?.IconUrl);
            if (icon != null && IsInsideCatalog(icon) && !Items.Any(i => string.Equals(ToLocalPath(i.Entry?.IconUrl), icon, StringComparison.OrdinalIgnoreCase)))
                DeleteOnSave(icon);

            foreach (var p in packages) DeleteOnSave(p);
        }

        private void DeleteOnSave(string path)
        {
            // A package added in this session was never copied: just forget it.
            if (_pendingCopies.Remove(path) && !File.Exists(path)) return;
            if (File.Exists(path)) _pendingDeletes.Add(path);
        }

        #endregion

        #region Save

        /// <summary>The changes <see cref="Save"/> would make, for confirmation.</summary>
        public List<string> DescribePendingChanges()
        {
            var lines = new List<string>();
            if (IndexDirty) lines.Add("Write " + IndexFileName);
            foreach (var item in Items.Where(i => i.ManifestDirty))
            {
                var manifestPath = item.ManifestPath
                                   ?? throw new InvalidOperationException("A dirty manifest has no local path.");
                lines.Add("Write " + RelativeUrl(manifestPath));
            }

            lines.AddRange(_pendingCopies.Keys.Select(p => "Copy package " + RelativeUrl(p)));
            lines.AddRange(_pendingDeletes.Select(p => "Delete " + RelativeUrl(p)));
            return lines;
        }

        /// <summary>
        /// Writes everything that changed: packages first, then manifests, then the
        /// index, and deletions last — so at no point does the index reference a
        /// file that is not there yet.
        /// </summary>
        public void Save()
        {
            foreach (var copy in _pendingCopies.ToList())
            {
                var destinationFolder = Path.GetDirectoryName(copy.Key)
                                        ?? throw new InvalidOperationException("A package destination has no containing folder.");
                Directory.CreateDirectory(destinationFolder);
                File.Copy(copy.Value, copy.Key, true);
                _pendingCopies.Remove(copy.Key);
            }

            foreach (var item in Items.Where(i => i.ManifestDirty))
            {
                var manifestPath = item.ManifestPath
                                   ?? throw new InvalidOperationException("A dirty manifest has no local path.");
                var manifest = item.Manifest
                               ?? throw new InvalidOperationException("A dirty manifest has no content.");
                CatalogJson.WriteIfChanged(manifestPath, manifest);
                item.ManifestDirty = false;
            }

            if (IndexDirty)
            {
                _index.SchemaVersion = _index.SchemaVersion == 0 ? 1 : _index.SchemaVersion;
                _index.Generated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                _index.AddOns = Items.Where(i => i.Listed).Select(i => i.Entry).ToList();
                CatalogJson.WriteIfChanged(IndexPath, _index);
                IndexDirty = false;
            }

            foreach (var path in _pendingDeletes.ToList())
            {
                if (File.Exists(path)) File.Delete(path);
                _pendingDeletes.Remove(path);
                RemoveEmptyFolders(Path.GetDirectoryName(path));
            }
        }

        /// <summary>Removes an emptied addons/{GUID}/ (icon) folder, never the catalog's own folders.</summary>
        private void RemoveEmptyFolders(string? folder)
        {
            var keep = new[] { RootFolder, Path.Combine(RootFolder, AddOnsFolder), Path.Combine(RootFolder, PackagesFolder) };
            while (folder != null && IsInsideCatalog(folder) && !keep.Any(k => string.Equals(k.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) &&
                   Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
                folder = Path.GetDirectoryName(folder);
            }
        }

        #endregion

        #region Helpers

        public CatalogItem? Find(string? id) => Items.FirstOrDefault(i => ManagerPaths.SameId(i.Id, id));

        private static CatalogEntry NewEntry(AddOnManifest manifest, string manifestUrl) => new CatalogEntry
        {
            Id = ManagerPaths.NormalizeId(manifest.Id) ?? string.Empty,
            Name = manifest.Name ?? string.Empty,
            Publisher = manifest.Publisher?.Name,
            Summary = Summarise(manifest.Description),
            ManifestUrl = manifestUrl
        };

        /// <summary>The first sentence of a description, as index.json's one-line summary.</summary>
        private static string? Summarise(string? description)
        {
            if (description == null || string.IsNullOrWhiteSpace(description)) return null;
            var text = description.Trim().Split('\n')[0].Trim();
            var stop = text.IndexOf(". ", StringComparison.Ordinal);
            if (stop > 0) text = text.Substring(0, stop);
            text = text.TrimEnd('.');
            return text.Length > 140 ? text.Substring(0, 137) + "..." : text;
        }

        private static void RequireEditable(CatalogItem item)
        {
            if (!item.IsEditable)
                throw new InvalidOperationException($"{item.Name}: {item.ManifestError ?? "the manifest is not editable"}.");
        }

        private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

        private static string? NullIfEmpty(string? value) => value == null || string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        #endregion
    }
}