using DeveMobileLPR.App.Controls;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class DualCameraPreviewPage : ContentPage
{
    private static readonly CameraOption[] Cameras =
    [
        new("2", "main · full mode", true),
        new("3", "ultrawide · full mode", false),
        new("4", "tele · full mode", true),
        new("5", "main · cropped mode", false),
        new("6", "tele · cropped mode", false),
        new("9", "ultrawide · cropped mode", false)
    ];

    private static readonly ResolutionOption[] Resolutions =
    [
        new("1280×720 · multi-stream compatibility", 1280, 720),
        new("1920×1080 · Full HD test", 1920, 1080),
        new("3840×2160 · 4K test", 3840, 2160)
    ];

    private readonly Dictionary<string, CheckBox> _cameraChecks = [];
    private readonly DualCameraPreview _preview;
    private readonly Picker _resolutionPicker;
    private readonly Label _statusLabel;

    public DualCameraPreviewPage()
    {
        Title = "Multi rear camera test";
        BackgroundColor = Color.FromArgb("#0B0D10");

        _resolutionPicker = new Picker
        {
            Title = "Requested preview resolution",
            ItemsSource = Resolutions,
            ItemDisplayBinding = new Binding(nameof(ResolutionOption.Label)),
            SelectedIndex = 1,
            TextColor = Colors.White
        };

        _statusLabel = new Label
        {
            Text = "Select 2–4 physical IDs and tap Start. Any Camera2/HAL rejection will appear here.",
            FontFamily = "monospace",
            FontSize = 12,
            TextColor = Color.FromArgb("#EEF1F4")
        };

        _preview = new DualCameraPreview
        {
            HeightRequest = 520,
            BackgroundColor = Colors.Black
        };
        _preview.StatusChanged += (_, status) => _statusLabel.Text = status;

        var startButton = new Button
        {
            Text = "Start / retry",
            BackgroundColor = Color.FromArgb("#F5B942"),
            TextColor = Color.FromArgb("#111318")
        };
        startButton.Clicked += StartClicked;

        var stopButton = new Button
        {
            Text = "Stop",
            BackgroundColor = Color.FromArgb("#252A31"),
            TextColor = Colors.White
        };
        stopButton.Clicked += (_, _) =>
        {
            _preview.Stop();
            _statusLabel.Text = "Stopped.";
        };

        var buttons = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 10
        };
        buttons.Add(startButton);
        buttons.Add(stopButton, 1);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(14, 10, 14, 28),
                Spacing = 12,
                Children =
                {
                    new Label
                    {
                        Text = "Select physical lenses behind logical rear camera 0",
                        FontSize = 21,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White
                    },
                    new Label
                    {
                        Text = "CameraX supports only two physical cameras. This screen uses lower-level Camera2 to test whether this Pixel accepts up to four physical preview streams.",
                        FontSize = 13,
                        TextColor = Color.FromArgb("#C8CFD7")
                    },
                    CreateCameraChoices(),
                    _resolutionPicker,
                    buttons,
                    CreatePreviewLabel("Panels follow selected ID order · each preview is labelled"),
                    _preview,
                    new Border
                    {
                        Padding = 12,
                        BackgroundColor = Color.FromArgb("#171A1F"),
                        Stroke = Color.FromArgb("#343A43"),
                        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                        Content = _statusLabel
                    },
                    new Label
                    {
                        Text = "Start with 2 + 4. For four streams, select 2 + 3 + 4 + one cropped mode and try 720p first. A configured session proves simultaneous previews only; continuous YUV analysis has separate bandwidth limits.",
                        FontSize = 12,
                        TextColor = Color.FromArgb("#AAB2BD")
                    }
                }
            }
        };

        ToolbarItems.Add(new ToolbarItem("Close", null, async () => await Navigation.PopAsync()));
    }

    protected override void OnDisappearing()
    {
        _preview.Stop();
        base.OnDisappearing();
    }

    private async void StartClicked(object? sender, EventArgs args)
    {
        var permission = await Permissions.RequestAsync<Permissions.Camera>();
        if (permission != PermissionStatus.Granted)
        {
            _statusLabel.Text = "Camera permission was not granted.";
            return;
        }

        var selectedIds = Cameras
            .Where(camera => _cameraChecks[camera.Id].IsChecked)
            .Select(camera => camera.Id)
            .ToArray();
        if (selectedIds.Length is < 2 or > 4)
        {
            _statusLabel.Text = $"Select between 2 and 4 cameras. Currently selected: {selectedIds.Length}.";
            return;
        }

        if (_resolutionPicker.SelectedItem is not ResolutionOption resolution)
        {
            _statusLabel.Text = "Select a resolution.";
            return;
        }

        _statusLabel.Text =
            $"Opening logical camera 0 with physical IDs {string.Join(" + ", selectedIds)} at {resolution.Width}×{resolution.Height}…";
        _preview.Start(selectedIds, resolution.Width, resolution.Height);
    }

    private View CreateCameraChoices()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            RowSpacing = 4,
            ColumnSpacing = 8
        };

        for (var index = 0; index < Cameras.Length; index++)
        {
            var camera = Cameras[index];
            var checkBox = new CheckBox
            {
                IsChecked = camera.SelectedByDefault,
                Color = Color.FromArgb("#F5B942")
            };
            _cameraChecks.Add(camera.Id, checkBox);
            var choice = new HorizontalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    checkBox,
                    new Label
                    {
                        Text = $"ID {camera.Id} · {camera.Description}",
                        VerticalTextAlignment = TextAlignment.Center,
                        FontSize = 12,
                        TextColor = Colors.White
                    }
                }
            };
            grid.Add(choice, index % 2, index / 2);
        }

        return grid;
    }

    private static Label CreatePreviewLabel(string text) =>
        new()
        {
            Text = text,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
            TextColor = Color.FromArgb("#F5B942")
        };

    private sealed record CameraOption(string Id, string Description, bool SelectedByDefault);

    private sealed record ResolutionOption(string Label, int Width, int Height)
    {
        public override string ToString() => Label;
    }
}
