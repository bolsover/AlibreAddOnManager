using System;
using System.Windows.Forms;
using AlibreAddOn;
using AlibreAddOnManager.Ui;
using AlibreX;

namespace AlibreAddOnManager
{
    /// <summary>
    /// IAlibreAddOn for the add-on manager: one root menu with one stateless
    /// command that opens the manager window modally and returns no command object.
    /// Available in every workspace, including Home — it never needs a session.
    /// </summary>
    public class AddOnManagerAddOn : IAlibreAddOn
    {
        public const string Title = "Manage Add-Ons";

        private const int MenuIdRoot = 7301;
      
        private readonly int _build;

        private readonly IADRoot _root;
        private readonly IntPtr _parentWindow;

        public AddOnManagerAddOn(IADRoot root, IntPtr parentWindow)
        {
            _root = root;
            _parentWindow = parentWindow;
            _build = AlibreVersion.GetBuild(root);
        }

        public int RootMenuItem => MenuIdRoot;

        public bool HasSubMenus(int menuId)
        {
            return menuId == MenuIdRoot;
        }
        private bool IsSupportedVersion => _build >= AlibreVersion.MinimumBuild;
        public Array SubMenuItems(int menuId) => null;


        public string MenuItemText(int menuId)=> menuId == MenuIdRoot ? Title : string.Empty;

        public bool PopupMenu(int menuId) => menuId == MenuIdRoot;

        // Enabled everywhere, root included, without touching Sessions — the Home
        // window passes an empty session identifier, which Sessions.Item rejects.
        public ADDONMenuStates MenuItemState(int menuId, string sessionIdentifier) => ADDONMenuStates.ADDON_MENU_ENABLED;

        public string MenuItemToolTip(int menuId)
        {
            return "Install, update, disable and remove Alibre Design add-ons";
        }

        /// <summary>Icon file name, looked up in the folder holding the .adc.</summary>
        public string MenuIcon(int menuId) => "AlibreAddOnManager.ico";

        public bool HasPersistentDataToSave(string sessionIdentifier) => false;

        public IAlibreAddOnCommand InvokeCommand(int menuId, string sessionIdentifier)
        {
          
            ShowWindow();

            return null;
        }

        private ManagerForm? _form;
        
        public void ShowWindow()
        {
            try
            {
                string version = null;
                try { version = _root?.Version; }
                catch (Exception) { /* version is informational only */ }
                
                if (!IsSupportedVersion)
                {
                    MessageBox.Show(MenuItemToolTip(MenuIdRoot), Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (_form != null && !_form.IsDisposed)
                {
                    if (_form.WindowState == FormWindowState.Minimized)
                        _form.WindowState = FormWindowState.Normal;
                    _form.Activate();
                    return;
                }

                _form = new ManagerForm(version);
                _form.FormClosed += (sender, args) =>  _form = null;
              
                if (_parentWindow != IntPtr.Zero)
                {
                    _form.Show(new WindowHandle(_parentWindow));
                }
                else
                {
                    _form.Show();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        // private void onFormClosed(object sender, FormClosedEventArgs e)
        // {
        //     _form = null;
        //     MenuItemState(menuId, sessionIdentifier) = ADDONMenuStates.ADDON_MENU_ENABLED
        // }

        public void LoadData(IStream pCustomData, string sessionIdentifier)
        {
        }

        public void SaveData(IStream pCustomData, string sessionIdentifier)
        {
        }

        public void setIsAddOnLicensed(bool isLicensed)
        {
        }

        public bool UseDedicatedRibbonTab() => false;
    }
    internal sealed class WindowHandle : IWin32Window
    {
        public IntPtr Handle { get; }
        public WindowHandle(IntPtr handle) => Handle = handle;
    }
}
