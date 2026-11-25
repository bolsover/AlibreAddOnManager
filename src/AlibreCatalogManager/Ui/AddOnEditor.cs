using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using AlibreAddOnManager.Models;
using AlibreCatalogManager.Catalog;

namespace AlibreCatalogManager.Ui
{
    /// <summary>
    /// Property-editor view of one add-on. Name and publisher live in both index.json
    /// and the manifest; editing them here keeps the two in step.
    /// </summary>
    public sealed class AddOnEditor
    {
        private const string Listing = "Listing (index.json)";
        private const string ManifestCategory = "Manifest (addons/{GUID}.json)";

        private readonly CatalogDocument _doc;
        private readonly CatalogItem _item;
        private readonly Action _changed;

        public AddOnEditor(CatalogDocument doc, CatalogItem item, Action changed)
        {
            _doc = doc;
            _item = item;
            _changed = changed;
        }

        private CatalogEntry Entry => _item.Entry;
        private AddOnManifest? Manifest => _item.Manifest;

        [Category(Listing), Description("The .adc Identifier GUID — the add-on's identity everywhere.")]
        public string Id => _item.Id;

        [Category(Listing), Description("Whether the add-on appears in index.json.")]
        public string Status => _item.Listed ? "Listed" : "Unlisted";

        [Category(Listing), Description("Display name, in both index.json and the manifest.")]
        public string? Name
        {
            get => Entry.Name ?? Manifest?.Name ?? _item.Id;
            set
            {
                value = Clean(value);
                Entry.Name = value;
                if (Manifest != null) Manifest.Name = value;
                Changed(true, true);
            }
        }

        [Category(Listing), Description("Publisher name, in both index.json and the manifest.")]
        public string? Publisher
        {
            get => Entry.Publisher ?? Manifest?.Publisher?.Name;
            set
            {
                value = Clean(value);
                Entry.Publisher = value;
                var publisher = EnsurePublisher();
                if (publisher != null) publisher.Name = value;

                Changed(true, true);
            }
        }

        [Category(Listing), Description("One line shown in the manager's Available list.")]
        public string? Summary
        {
            get => Entry.Summary;
            set
            {
                Entry.Summary = Clean(value);
                Changed(true, false);
            }
        }

        [Category(Listing), Description("Comma-separated, e.g. modeling, utilities.")]
        public string Tags
        {
            get => Join(Entry.Tags);
            set
            {
                Entry.Tags = Split(value);
                Changed(true, false);
            }
        }

        [Category(Listing), Description("Comma-separated .adc workspace types: Always, Part, SheetMetal, Assembly, Drawing, Home.")]
        public string Workspaces
        {
            get => Join(Entry.Workspaces);
            set
            {
                Entry.Workspaces = Split(value);
                Changed(true, false);
            }
        }

        [Category(Listing), Description("Relative to index.json, e.g. addons/{GUID}/icon.png. Optional.")]
        public string? IconUrl
        {
            get => Entry.IconUrl;
            set
            {
                Entry.IconUrl = Clean(value);
                Changed(true, false);
            }
        }

        [Category(Listing), Description("Read-only: always the newest version in the manifest.")]
        public string? LatestVersion => Entry.LatestVersion;

        [Category(ManifestCategory), Description("Deploy folder under %ProgramData%\\Alibre AddOns. Changing it after release strands existing installs.")]
        public string? FolderName
        {
            get => Manifest?.FolderName;
            set
            {
                if (Manifest != null) Manifest.FolderName = Clean(value);
                Changed(false, true);
            }
        }

        [Category(ManifestCategory)]
        public string? PublisherUrl
        {
            get => Manifest?.Publisher?.Url;
            set
            {
                var publisher = EnsurePublisher();
                if (publisher != null) publisher.Url = Clean(value);
                Changed(false, true);
            }
        }

        [Category(ManifestCategory)]
        public string? PublisherEmail
        {
            get => Manifest?.Publisher?.Email;
            set
            {
                var publisher = EnsurePublisher();
                if (publisher != null) publisher.Email = Clean(value);
                Changed(false, true);
            }
        }

        [Category(ManifestCategory), Description("SPDX identifier, e.g. MIT.")]
        public string? License
        {
            get => Manifest?.License;
            set
            {
                if (Manifest != null) Manifest.License = Clean(value);
                Changed(false, true);
            }
        }

        [Category(ManifestCategory)]
        public string? Homepage
        {
            get => Manifest?.Homepage;
            set
            {
                if (Manifest != null) Manifest.Homepage = Clean(value);
                Changed(false, true);
            }
        }

        [Category(ManifestCategory), Description("Longer description (markdown).")]
        [Multiline]
        public string? Description
        {
            get => Manifest?.Description;
            set
            {
                if (Manifest != null) Manifest.Description = value == null || string.IsNullOrWhiteSpace(value) ? null : value;
                Changed(false, true);
            }
        }

        private Publisher? EnsurePublisher()
        {
            var manifest = Manifest;
            if (manifest == null) return null;
            var publisher = manifest.Publisher;
            if (publisher == null)
            {
                publisher = new Publisher { Name = Entry.Publisher };
                manifest.Publisher = publisher;
            }

            return publisher;
        }

        private void Changed(bool index, bool manifest)
        {
            if (index && _item.Listed) _doc.MarkIndexDirty();
            if (manifest && _item.IsEditable) _doc.MarkManifestDirty(_item);
            _changed?.Invoke();
        }

        internal static string? Clean(string? value) => value == null || string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        internal static string Join(List<string>? values) => values == null ? string.Empty : string.Join(", ", values);

        internal static List<string>? Split(string? value)
        {
            var list = (value ?? string.Empty).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
            return list.Count == 0 ? null : list;
        }
    }
}