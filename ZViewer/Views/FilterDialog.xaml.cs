using System.Windows;
using ZViewer.Models;

namespace ZViewer.Views
{
    public partial class FilterDialog : Window
    {
        public FilterCriteria? FilterCriteria { get; private set; }

        public FilterDialog()
        {
            InitializeComponent();
            InitializeControls();
        }

        private void InitializeControls()
        {
            OkButton.Click += OkButton_Click;
            CancelButton.Click += CancelButton_Click;
            ClearButton.Click += ClearButton_Click;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            FilterCriteria = new FilterCriteria
            {
                TimeRange = LoggedComboBox.SelectedIndex,
                IncludeCritical = CriticalCheckBox.IsChecked == true,
                IncludeError = ErrorCheckBox.IsChecked == true,
                IncludeWarning = WarningCheckBox.IsChecked == true,
                IncludeInformation = InformationCheckBox.IsChecked == true,
                IncludeVerbose = VerboseCheckBox.IsChecked == true,
                EventIds = EventIdsTextBox.Text,
                TaskCategory = TaskCategoryComboBox.Text,
                Keywords = KeywordsComboBox.Text,
                User = UserTextBox.Text,
                Computer = ComputerTextBox.Text
            };

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            LoggedComboBox.SelectedIndex = 0;
            CriticalCheckBox.IsChecked = false;
            ErrorCheckBox.IsChecked = false;
            WarningCheckBox.IsChecked = false;
            InformationCheckBox.IsChecked = false;
            VerboseCheckBox.IsChecked = false;
            EventIdsTextBox.Text = "<All Event IDs>";
            TaskCategoryComboBox.Text = "";
            KeywordsComboBox.Text = "";
            UserTextBox.Text = "<All Users>";
            ComputerTextBox.Text = "<All Computers>";
        }
    }
}