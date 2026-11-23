using System.Collections.Generic;
using System.Runtime.Serialization;

namespace AlibreAddOnManager.Models
{
    /// <summary>Per-user settings: %APPDATA%\AlibreAddOnManager\settings.json.</summary>
    [DataContract]
    public class ManagerSettings
    {
        [DataMember(Name = "catalogUrl", EmitDefaultValue = false)] public string CatalogUrl { get; set; }

        /// <summary>Previously loaded catalog locations, most recent first. May be null after deserialization.</summary>
        [DataMember(Name = "recentCatalogUrls", EmitDefaultValue = false)] public List<string> RecentCatalogUrls { get; set; }
    }
}
