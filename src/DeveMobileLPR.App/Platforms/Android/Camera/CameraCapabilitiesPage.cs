namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class CameraCapabilitiesPage : ContentPage
{
    private readonly Label _statusLabel = null!;
    private readonly VerticalStackLayout _reportLayout;
    private string _plainTextReport = string.Empty;

    public CameraCapabilitiesPage()
    {
        Title = "Camera capabilities";
        BackgroundColor = Color.FromArgb("#0B0D10");

        var refreshButton = new Button
        {
            Text = "Refresh",
            HeightRequest = 42,
            BackgroundColor = Color.FromArgb("#F5B942"),
            TextColor = Color.FromArgb("#111318")
        };
        refreshButton.Clicked += async (_, _) => await RefreshAsync();

        var copyButton = new Button
        {
            Text = "Copy full report",
            HeightRequest = 42,
            BackgroundColor = Color.FromArgb("#252A31"),
            TextColor = Colors.White
        };
        copyButton.Clicked += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_plainTextReport))
            {
                await Clipboard.Default.SetTextAsync(_plainTextReport);
                _statusLabel.Text = "Copied. You can paste the report into the chat too.";
            }
        };

        _statusLabel = new Label
        {
            Text = "Reading Camera2 metadata…",
            FontSize = 13,
            TextColor = Color.FromArgb("#AAB2BD")
        };
        _reportLayout = new VerticalStackLayout { Spacing = 12 };

        var buttonGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 10
        };
        buttonGrid.Add(refreshButton);
        buttonGrid.Add(copyButton, 1);

        var content = new VerticalStackLayout
        {
            Padding = new Thickness(16, 12, 16, 28),
            Spacing = 14,
            Children =
            {
                new Label
                {
                    Text = "What Android exposes on this phone",
                    FontSize = 24,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Colors.White
                },
                new Label
                {
                    Text = "Public IDs are cameras an app can open. Physical IDs are the real lenses behind a logical camera. Concurrent sets are combinations Android promises can run together.",
                    FontSize = 14,
                    TextColor = Color.FromArgb("#D1D6DC")
                },
                buttonGrid,
                _statusLabel,
                _reportLayout
            }
        };

        Content = new ScrollView { Content = content };
        ToolbarItems.Add(new ToolbarItem("Close", null, async () => await Navigation.PopModalAsync()));
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _statusLabel.Text = "Reading Camera2 metadata…";
        _reportLayout.Children.Clear();

        try
        {
            var report = await Task.Run(CameraCapabilitiesReport.Read);
            _plainTextReport = report.PlainText;
            foreach (var section in report.Sections)
            {
                _reportLayout.Children.Add(CreateSection(section));
            }

            _statusLabel.Text = $"Read {report.CameraCount} public camera(s) at {DateTime.Now:HH:mm:ss}. Take a scrolling screenshot, or copy the full report.";
        }
        catch (Exception exception)
        {
            _plainTextReport = exception.ToString();
            _statusLabel.Text = $"Could not read camera metadata: {exception.Message}";
            _reportLayout.Children.Add(CreateSection(new CameraReportSection("ERROR", exception.ToString())));
        }
    }

    private static Border CreateSection(CameraReportSection section) => new()
    {
        Padding = 14,
        BackgroundColor = Color.FromArgb("#171A1F"),
        Stroke = Color.FromArgb("#343A43"),
        StrokeThickness = 1,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
        Content = new VerticalStackLayout
        {
            Spacing = 7,
            Children =
            {
                new Label
                {
                    Text = section.Title,
                    FontSize = 16,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#F5B942")
                },
                new Label
                {
                    Text = section.Body,
                    FontFamily = "monospace",
                    FontSize = 12,
                    LineBreakMode = LineBreakMode.WordWrap,
                    TextColor = Color.FromArgb("#EEF1F4")
                }
            }
        }
    };
}
