using System;
using System.Reflection;
using System.Windows.Forms;

namespace AlibreAddOnManager.Ui
{
    /// <summary>
    /// Modal "About" dialog. The application name, version and copyright are read
    /// at runtime from this assembly's metadata (Properties\AssemblyInfo.cs).
    /// </summary>
    public partial class AboutForm : Form
    {
        public AboutForm()
        {
            InitializeComponent();

            var assembly = typeof(AboutForm).Assembly;
            var name = assembly.GetName();

            lblName.Text = AttributeValue<AssemblyProductAttribute>(assembly, a => a.Product, name.Name);
            lblVersion.Text = "Version " + name.Version;
            lblCopyright.Text = AttributeValue<AssemblyCopyrightAttribute>(assembly, a => a.Copyright, string.Empty);
            Text = "About " + lblName.Text;
        }

        private static string AttributeValue<T>(Assembly assembly, Func<T, string> selector, string fallback)
            where T : Attribute
        {
            var attribute = (T)Attribute.GetCustomAttribute(assembly, typeof(T));
            var value = attribute == null ? null : selector(attribute);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
