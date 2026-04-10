using System;
using System.IO;
using System.Reflection;
using System.Windows;

namespace AdvancedLandDevTools.UI
{
    public partial class AldtHelpWindow : Window
    {
        public AldtHelpWindow()
        {
            InitializeComponent();
            LoadHelp();
        }

        private void LoadHelp()
        {
            // Help.htm is deployed next to the DLL in Contents\
            string dllDir  = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location) ?? "";
            string helpPath = Path.Combine(dllDir, "Help.htm");

            if (File.Exists(helpPath))
            {
                HelpBrowser.Navigate(new Uri(helpPath));
            }
            else
            {
                // Fallback: show an inline message so the window still opens
                string fallback = Path.GetTempFileName() + ".html";
                File.WriteAllText(fallback,
                    "<html><body style='background:#1a1a1a;color:#e0e0e0;" +
                    "font-family:Segoe UI;padding:40px'>" +
                    "<h2 style='color:#EF5350'>Help file not found</h2>" +
                    $"<p>Expected location:<br><code>{helpPath}</code></p>" +
                    "<p>Rebuild the project to re-copy Help.htm to the bundle.</p>" +
                    "</body></html>");
                HelpBrowser.Navigate(new Uri(fallback));
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Let the window actually close (not just hide)
            base.OnClosing(e);
        }
    }
}
