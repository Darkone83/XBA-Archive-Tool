using System.Windows;

namespace XbaTool
{
    public partial class AboutWindow : Window
    {
        public AboutWindow() { InitializeComponent(); }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    }
}
