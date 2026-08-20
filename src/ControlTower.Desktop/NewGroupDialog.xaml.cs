using System.Windows;

namespace ControlTower.Desktop
{
    public partial class NewGroupDialog : Window
    {
        public NewGroupDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => GroupNameBox.Focus();
        }

        public string GroupName { get; private set; } = string.Empty;

        private void OkClick(object sender, RoutedEventArgs e)
        {
            var name = (GroupNameBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            GroupName = name;
            DialogResult = true;
            Close();
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
