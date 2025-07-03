namespace ZViewer.Services
{
    public interface IErrorService
    {
        void HandleError(Exception exception, string context = "");
        void HandleError(string errorMessage, string context = "");
        Task ShowErrorAsync(string message, string title = "Error");
        Task ShowWarningAsync(string message, string title = "Warning");
        Task ShowInfoAsync(string message, string title = "Information");

        event EventHandler<string> StatusUpdated;
    }

    public class ErrorService : IErrorService
    {
        private readonly ILoggingService _loggingService;

        public ErrorService(ILoggingService loggingService)
        {
            _loggingService = loggingService;
        }

        public event EventHandler<string>? StatusUpdated;

        public void HandleError(Exception exception, string context = "")
        {
            var message = $"{context}: {exception.Message}";
            _loggingService.LogError(exception, "Error in {Context}", context);
            StatusUpdated?.Invoke(this, $"Error: {message}");
        }

        public void HandleError(string errorMessage, string context = "")
        {
            var message = string.IsNullOrEmpty(context) ? errorMessage : $"{context}: {errorMessage}";
            _loggingService.LogError("Error in {Context}: {Message}", context, errorMessage);
            StatusUpdated?.Invoke(this, $"Error: {message}");
        }

        public async Task ShowErrorAsync(string message, string title = "Error")
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error));
        }

        public async Task ShowWarningAsync(string message, string title = "Warning")
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning));
        }

        public async Task ShowInfoAsync(string message, string title = "Information")
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information));
        }
    }
}