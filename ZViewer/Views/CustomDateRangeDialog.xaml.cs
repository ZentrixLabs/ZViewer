using System;
using System.Windows;

namespace ZViewer.Views
{
    public partial class CustomDateRangeDialog : Window
    {
        public DateTime FromDate { get; private set; }
        public DateTime ToDate { get; private set; }

        public CustomDateRangeDialog()
        {
            InitializeComponent();

            // Set defaults
            ToDatePicker.SelectedDate = DateTime.Now;
            FromDatePicker.SelectedDate = DateTime.Now.AddDays(-7);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (FromDatePicker.SelectedDate.HasValue && ToDatePicker.SelectedDate.HasValue)
            {
                FromDate = FromDatePicker.SelectedDate.Value;
                ToDate = ToDatePicker.SelectedDate.Value.AddDays(1).AddSeconds(-1); // End of day

                if (FromDate > ToDate)
                {
                    MessageBox.Show("From date must be before To date.", "Invalid Date Range",
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DialogResult = true;
            }
            else
            {
                MessageBox.Show("Please select both From and To dates.", "Missing Dates",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}