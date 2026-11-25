using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AlibreAddOnManager.Models;

namespace AlibreAddOnManager
{
    /// <summary>
    /// Reads the static catalog (index.json + per-add-on manifests) and downloads
    /// packages. Accepts https:// and http:// URLs, file:// URLs and plain local
    /// paths (for a catalog on a share or in a checkout). Over http:// the sha256
    /// check offers no tamper protection, since the manifest carrying the hash
    /// arrives over the same unauthenticated channel.
    /// Catalog signature verification (design doc §3.3) is not implemented yet.
    /// </summary>
    public class CatalogClient
    {
        private static readonly HttpClient Http = CreateHttpClient();

        public Uri? IndexUri { get; private set; }

        public async Task<CatalogIndex> LoadIndexAsync(string location)
        {
            var indexUri = ToUri(location);
            IndexUri = indexUri;
            var data = await ReadAsync(indexUri).ConfigureAwait(false);

            if (IsHtmlOrXml(data))
            {
                throw new InvalidDataException(
                    $"The URL '{indexUri}' returned an HTML page or XML markup instead of JSON catalog data. Ensure the URL points to a raw catalog or valid index.json.");
            }

            CatalogIndex? index;
            try
            {
                index = Json.Parse<CatalogIndex>(data);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Failed to parse catalog index from '{indexUri}': {ex.Message}", ex);
            }

            if (index == null)
                throw new InvalidDataException($"The catalog index at '{indexUri}' was empty or invalid.");
            if (index.SchemaVersion != 1)
                throw new InvalidDataException($"Unsupported catalog schemaVersion {index.SchemaVersion}.");
            var addOns = index.AddOns ?? new List<CatalogEntry>();
            foreach (var entry in addOns)
            {
                if (entry == null) throw new InvalidDataException("The catalog contains an empty add-on entry.");
                var id = Required(entry.Id, "catalog add-on id");
                if (!Guid.TryParse(id, out _)) throw new InvalidDataException($"Catalog add-on id '{id}' is not a GUID.");
                Required(entry.Name, "catalog add-on name");
                Required(entry.ManifestUrl, "catalog manifestUrl");
            }
            index.AddOns = addOns;
            return index;
        }

        public async Task<AddOnManifest> LoadManifestAsync(CatalogEntry entry)
        {
            var entryId = Required(entry.Id, "catalog add-on id");
            var manifestUri = Resolve(Required(entry.ManifestUrl, "catalog manifestUrl"));
            var data = await ReadAsync(manifestUri).ConfigureAwait(false);

            if (IsHtmlOrXml(data))
            {
                throw new InvalidDataException(
                    $"The manifest for '{entry.Name}' at '{manifestUri}' returned an HTML page or XML markup instead of JSON.");
            }

            AddOnManifest? manifest;
            try
            {
                manifest = Json.Parse<AddOnManifest>(data);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(
                    $"Failed to parse manifest for '{entry.Name}' ({entry.Id}) from '{manifestUri}': {ex.Message}", ex);
            }

            if (manifest == null)
                throw new InvalidDataException($"The manifest for '{entry.Name}' at '{manifestUri}' was empty or invalid.");
            var manifestId = Required(manifest.Id, "manifest id");
            if (!Guid.TryParse(manifestId, out _)) throw new InvalidDataException($"Manifest id '{manifestId}' is not a GUID.");
            if (!ManagerPaths.SameId(manifestId, entryId))
                throw new InvalidDataException($"Manifest id {manifestId} does not match catalog entry {entryId}.");
            return manifest;
        }

        /// <summary>Downloads into the per-user cache and checks size and SHA-256.</summary>
        public async Task<string> DownloadAsync(AddOnManifest manifest, AddOnVersion version)
        {
            var sha256 = Required(version.Sha256, "package sha256");
            var packageUrl = Required(version.PackageUrl, "packageUrl");
            var manifestId = Required(manifest.Id, "manifest id");
            if (!Guid.TryParse(manifestId, out var manifestGuid))
                throw new InvalidDataException($"Manifest id '{manifestId}' is not a GUID.");
            var folderName = Required(manifest.FolderName, "manifest folderName");
            var versionName = Required(version.Version, "package version");

            var uri = Resolve(packageUrl);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                fileName = folderName + "-" + versionName + Packages.Extension;

            var directory = Path.Combine(ManagerPaths.CacheDirectory, manifestGuid.ToString("N"), SafeSegment(versionName));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);

            if (!File.Exists(path) || !HashMatches(path, sha256))
            {
                var data = await ReadAsync(uri).ConfigureAwait(false);
                if (version.Size.HasValue && data.LongLength != version.Size.Value)
                    throw new InvalidDataException($"Downloaded {data.LongLength} bytes; catalog says {version.Size.Value}.");
                File.WriteAllBytes(path, data);
            }

            if (!HashMatches(path, sha256))
            {
                File.Delete(path);
                throw new InvalidDataException("Downloaded package does not match the catalog sha256.");
            }

            return path;
        }

        /// <summary>Newest version compatible with the running build, else the newest overall.</summary>
        public static AddOnVersion? ChooseVersion(AddOnManifest manifest, int runningBuild)
        {
            var ordered = (manifest.Versions ?? new List<AddOnVersion>())
                .Where(v => v != null && !string.IsNullOrWhiteSpace(v.PackageUrl))
                .Where(v => string.IsNullOrEmpty(v.Kind) || v.Kind == "integrated")
                .OrderByDescending(v => v.Version, Comparer.Instance)
                .ToList();
            return ordered.FirstOrDefault(v => v.IsCompatibleWith(runningBuild)) ?? ordered.FirstOrDefault();
        }

        private Uri Resolve(string relativeOrAbsolute)
        {
            if (Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absolute) && absolute.Scheme != Uri.UriSchemeFile)
                return Check(absolute);
            var indexUri = IndexUri ?? throw new InvalidOperationException("Catalog index has not been loaded.");
            return Check(new Uri(indexUri, relativeOrAbsolute));
        }

        public static Uri ToUri(string location)
        {
            if (string.IsNullOrWhiteSpace(location))
                throw new ArgumentException("No catalog location set.");
            location = location.Trim();

            if (!location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !location.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !location.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (location.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase) ||
                    location.StartsWith("www.github.com/", StringComparison.OrdinalIgnoreCase) ||
                    location.StartsWith("raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
                {
                    location = "https://" + location;
                }
            }

            // Note "D:\catalog" also parses as an absolute file: URI, so file URIs
            // and plain paths share the local branch below.
            if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                {
                    if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase))
                    {
                        uri = NormalizeGitHubUri(uri);
                    }

                    uri = NormalizeWebDirectoryUri(uri);
                }

                return Check(uri);
            }

            // A local path: a folder means its index.json.
            var path = Path.GetFullPath(uri != null && uri.IsFile ? uri.LocalPath : location);
            if (Directory.Exists(path) || !Path.HasExtension(path))
                path = Path.Combine(path, "index.json");
            return new Uri(path);
        }

        private static Uri NormalizeGitHubUri(Uri uri)
        {
            var segments = uri.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                return uri;

            var owner = segments[0];
            var repo = segments[1];

            if (segments.Length == 2)
            {
                // e.g. https://github.com/owner/repo or https://github.com/owner/repo/
                return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/main/index.json");
            }

            var kind = segments[2];
            if (string.Equals(kind, "tree", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "blob", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "raw", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Length == 3)
                {
                    // e.g. https://github.com/owner/repo/tree
                    return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/main/index.json");
                }

                var branch = segments[3];
                var subpathParts = segments.Skip(4).ToArray();
                if (subpathParts.Length == 0)
                {
                    // e.g. https://github.com/owner/repo/tree/main
                    return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/index.json");
                }

                var subpath = string.Join("/", subpathParts);
                if (subpath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{subpath}");
                }

                return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{subpath}/index.json");
            }

            return uri;
        }

        private static Uri NormalizeWebDirectoryUri(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return uri;

            var path = uri.AbsolutePath;
            if (path.EndsWith("/"))
            {
                var builder = new UriBuilder(uri);
                builder.Path = path + "index.json";
                return builder.Uri;
            }

            if (!Path.HasExtension(path) && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri);
                builder.Path = path + "/index.json";
                return builder.Uri;
            }

            return uri;
        }

        private static Uri Check(Uri uri)
        {
            if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeFile)
                return uri;
            throw new InvalidOperationException($"Only http://, https:// or local catalogs are allowed (got {uri.Scheme}://).");
        }

        private static async Task<byte[]> ReadAsync(Uri uri)
        {
            if (uri.IsFile)
                return File.ReadAllBytes(uri.LocalPath);

            using (var response = await Http.GetAsync(uri).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType != null &&
                    (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        $"The URL '{uri}' returned an HTML page (Content-Type: {mediaType}) instead of JSON catalog data. Ensure the URL points to a raw catalog or valid index.json.");
                }

                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }

        private static bool IsHtmlOrXml(byte[] data)
        {
            if (data == null || data.Length == 0) return false;
            int i = 0;
            // Skip UTF-8 BOM if present
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                i = 3;
            while (i < data.Length && (data[i] == ' ' || data[i] == '\t' || data[i] == '\r' || data[i] == '\n'))
                i++;
            return i < data.Length && data[i] == '<';
        }

        private static bool HashMatches(string path, string sha256)
        {
            return string.Equals(Packages.Sha256File(path), sha256.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeSegment(string? text)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((text ?? "unknown").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return clean == "." || clean == ".." ? "_" : clean;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AlibreAddOnManager/1.0");
            return client;
        }

        private static string Required(string? value, string field)
        {
            if (value == null || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Catalog is missing '" + field + "'.");
            return value;
        }

        private sealed class Comparer : IComparer<string?>
        {
            public static readonly Comparer Instance = new Comparer();
            public int Compare(string? x, string? y) => Packages.CompareVersions(x, y);
        }
    }
}
