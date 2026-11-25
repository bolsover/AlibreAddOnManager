using System;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace AlibreCatalogManager.Ui
{
    /// <summary>Message boxes, file and folder pickers and the wait cursor, owned by the control's window.</summary>
    internal static class Dialogs
    {
        public static MessageBoxResult Show(DependencyObject owner, string text, string caption,
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        {
            var window = owner as Window ?? Window.GetWindow(owner);
            return window != null
                ? MessageBox.Show(window, text, caption, buttons, image)
                : MessageBox.Show(text, caption, buttons, image);
        }

        public static void Error(DependencyObject owner, string title, Exception e) =>
            Show(owner, e.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);

        /// <summary>A folder, or null if cancelled.</summary>
        public static string? BrowseFolder(DependencyObject owner, string title, string? initial = null)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = title };
            if (!string.IsNullOrWhiteSpace(initial) && System.IO.Directory.Exists(initial)) dialog.SelectedPath = initial;

            var window = Window.GetWindow(owner);
            var handle = window == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var result = handle == IntPtr.Zero ? dialog.ShowDialog() : dialog.ShowDialog(new WindowHandle(handle));
            return result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
        }

        /// <summary>A file, or null if cancelled.</summary>
        public static string? OpenFile(DependencyObject owner, string title, string filter, string? initialDirectory = null)
        {
            var dialog = new OpenFileDialog { Title = title, Filter = filter };
            if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
            return dialog.ShowDialog(Window.GetWindow(owner)) == true ? dialog.FileName : null;
        }

        /// <summary>Shows the wait cursor until disposed.</summary>
        public static IDisposable Busy()
        {
            Mouse.OverrideCursor = Cursors.Wait;
            return new RestoreCursor();
        }

        private sealed class RestoreCursor : IDisposable
        {
            public void Dispose() => Mouse.OverrideCursor = null;
        }

        private sealed class WindowHandle : System.Windows.Forms.IWin32Window
        {
            public WindowHandle(IntPtr handle) => Handle = handle;

            public IntPtr Handle { get; }
        }
    }
}