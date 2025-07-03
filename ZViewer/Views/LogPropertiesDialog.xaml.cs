using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ZViewer.Models;
using ZViewer.Services;

namespace ZViewer.Views
{
    public partial class LogPropertiesDialog : Window
    {
        private readonly LogProperties _originalProperties;
        private readonly ILogPropertiesService _logPropertiesService;
        private bool _hasChanges = false;

        public LogPropertiesDialog(LogProperties logProperties, ILogPropertiesService logPropertiesService)
        {
            InitializeComponent();
            _originalProperties = logProperties;
            _logPropertiesService = logPropertiesService;
            LoadLogProperties(logProperties);
            InitializeControls();
        }

        private void InitializeControls()
        {
            OkButton.Click += OkButton_Click;
            CancelButton.Click += (s, e) => DialogResult = false;
            ApplyButton.Click += ApplyButton_Click;

            // Track changes
            EnableLoggingCheckBox.Checked += (s, e) => MarkChanged();
            EnableLoggingCheckBox.Unchecked += (s, e) => MarkChanged();
            OverwriteRadio.Checked += (s, e) => MarkChanged();
            ArchiveRadio.Checked += (s, e) => MarkChanged();
            DoNotOverwriteRadio.Checked += (s, e) => MarkChanged();
        }

        private void LoadLogProperties(LogProperties props)
        {
            FullNameTextBox.Text = props.DisplayName;
            LogPathTextBox.Text = props.LogPath;
            LogSizeTextBox.Text = props.LogSizeFormatted;
            CreatedTextBox.Text = props.Created?.ToString("dddd, MMMM dd, yyyy h:mm:ss tt") ?? "Unknown";
            ModifiedTextBox.Text = props.Modified?.ToString("dddd, MMMM dd, yyyy h:mm:ss tt") ?? "Unknown";
            AccessedTextBox.Text = props.Accessed?.ToString("dddd, MMMM dd, yyyy h:mm:ss tt") ?? "Unknown";

            EnableLoggingCheckBox.IsChecked = props.LoggingEnabled;
            MaxSizeTextBox.Text = props.MaximumSizeKB.ToString();

            // Set the retention policy radio button
            switch (props.RetentionPolicy)
            {
                case "Overwrite":
                    OverwriteRadio.IsChecked = true;
                    break;
                case "Archive":
                    ArchiveRadio.IsChecked = true;
                    break;
                case "Manual":
                    DoNotOverwriteRadio.IsChecked = true;
                    break;
            }
        }

        private void MarkChanged()
        {
            _hasChanges = true;
            ApplyButton.IsEnabled = true;
        }

        private void MaxSizeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            MarkChanged();
        }

        private void IncreaseSizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(MaxSizeTextBox.Text, out int currentSize))
            {
                MaxSizeTextBox.Text = Math.Min(currentSize + 1024, int.MaxValue / 1024).ToString();
            }
        }

        private void DecreaseSizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(MaxSizeTextBox.Text, out int currentSize))
            {
                MaxSizeTextBox.Text = Math.Max(currentSize - 1024, 1024).ToString();
            }
        }

        private async void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                $"Are you sure you want to clear the {_originalProperties.LogName} log?\n\nThis action cannot be undone.",
                "Clear Log",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var success = await _logPropertiesService.ClearLogAsync(_originalProperties.LogName);
                if (success)
                {
                    MessageBox.Show("Log cleared successfully.", "Clear Log", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            await ApplyChanges();
        }

        private async void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_hasChanges)
            {
                var success = await ApplyChanges();
                if (!success) return;
            }
            DialogResult = true;
        }

        private async Task<bool> ApplyChanges()
        {
            try
            {
                var updatedProperties = new LogProperties
                {
                    LogName = _originalProperties.LogName,
                    LoggingEnabled = EnableLoggingCheckBox.IsChecked == true,
                    MaximumSizeKB = int.TryParse(MaxSizeTextBox.Text, out int size) ? Math.Max(size, 1024) : _originalProperties.MaximumSizeKB,
                    RetentionPolicy = GetSelectedRetentionPolicy()
                };

                var success = await _logPropertiesService.UpdateLogPropertiesAsync(_originalProperties.LogName, updatedProperties);

                if (success)
                {
                    _hasChanges = false;
                    ApplyButton.IsEnabled = false;
                    MessageBox.Show("Log properties updated successfully.", "Update Properties", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return success;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to apply changes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private string GetSelectedRetentionPolicy()
        {
            if (OverwriteRadio.IsChecked == true) return "Overwrite";
            if (ArchiveRadio.IsChecked == true) return "Archive";
            if (DoNotOverwriteRadio.IsChecked == true) return "Manual";
            return "Overwrite";
        }
    }
}