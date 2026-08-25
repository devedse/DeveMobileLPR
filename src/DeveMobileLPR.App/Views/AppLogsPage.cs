using DeveMobileLPR.App.Services;

namespace DeveMobileLPR.App.Views;

internal sealed class AppLogsPage : ContentPage
{
    private readonly AppLogService _log;
    private readonly Editor _contents;

    public AppLogsPage(AppLogService log)
    {
        _log = log;
        Title = "App logs";
        BackgroundColor = Color.FromArgb("#090B0F");
        _contents = new Editor
        {
            IsReadOnly = true,
            AutoSize = EditorAutoSizeOption.Disabled,
            FontFamily = "monospace",
            FontSize = 11,
            TextColor = Color.FromArgb("#E8EDF5"),
            BackgroundColor = Color.FromArgb("#11151B")
        };

        var copy = new Button { Text = "Copy all" };
        copy.Clicked += async (_, _) => await Clipboard.Default.SetTextAsync(_contents.Text ?? string.Empty);
        var clear = new Button { Text = "Clear" };
        clear.Clicked += async (_, _) =>
        {
            if (await DisplayAlertAsync("Clear app logs?", "This removes the persistent diagnostic history from this device.", "Clear", "Cancel"))
            {
                _log.Clear();
                Refresh();
            }
        };
        var close = new Button { Text = "Close" };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();

        var actions = new HorizontalStackLayout
        {
            Spacing = 10,
            HorizontalOptions = LayoutOptions.End,
            Children = { copy, clear, close }
        };
        Content = new Grid
        {
            Padding = new Thickness(18),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Children =
            {
                new Label
                {
                    Text = "Persistent diagnostics",
                    FontAttributes = FontAttributes.Bold,
                    FontSize = 24,
                    TextColor = Colors.White
                },
                _contents,
                actions
            }
        };
        _contents.SetValue(Grid.RowProperty, 1);
        actions.SetValue(Grid.RowProperty, 2);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AppLogService.Changed += LogChanged;
        Refresh();
    }

    protected override void OnDisappearing()
    {
        AppLogService.Changed -= LogChanged;
        base.OnDisappearing();
    }

    private void LogChanged(object? sender, EventArgs args) => MainThread.BeginInvokeOnMainThread(Refresh);

    private void Refresh() => _contents.Text = string.Join(Environment.NewLine, _log.ReadRecent());
}
