using System.Text.RegularExpressions;
using AlibreX;

namespace AlibreAddOnManager.Ui
{

    public class AlibreVersion
    {
        public const int MinimumBuild = 29000;

        /// <summary>
        /// The build number is the trailing number of IADRoot.Version
        /// (e.g. "29.1.0.29127" → 29127). Returns 0 if it cannot be read.
        /// </summary>
        public static int GetBuild(IADRoot? root)
        {
            string? version;
            try
            {
                version = root?.Version;
            }
            catch
            {
                return 0;
            }

            if (string.IsNullOrEmpty(version)) return 0;

            var numbers = Regex.Matches(version, @"\d+");
            if (numbers.Count == 0) return 0;
            return int.TryParse(numbers[numbers.Count - 1].Value, out int build) ? build : 0;
        }
    }
}