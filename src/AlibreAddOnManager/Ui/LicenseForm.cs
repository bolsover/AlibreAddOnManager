using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AlibreAddOnManager.Ui
{
    /// <summary>
    /// Modal dialog showing the MIT license. The text is the repository's LICENSE file,
    /// embedded in this assembly under the resource name "LICENSE".
    /// </summary>
    public partial class LicenseForm : Form
    {
        private const string ResourceName = "LICENSE";

        public LicenseForm()
        {
            InitializeComponent();
            txtLicense.Text = LoadLicenseText();
            txtLicense.Select(0, 0);
        }

        private static string LoadLicenseText()
        {
            using (var stream = typeof(LicenseForm).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) return "The license text could not be found in this assembly.";
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    return reader.ReadToEnd().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            }
        }
    }
}
