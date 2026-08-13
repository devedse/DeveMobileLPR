using DeveMobileLPR.App.Controls;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class DualCameraPreviewPage : ContentPage
{
    private static readonly CameraPairOption[] CameraPairs =
    [
        new("2 + 4 · full main + full tele", "2", "4"),
        new("5 + 4 · cropped main mode + full tele", "5", "4"),
        new("2 + 6 · full main + cropped tele mode", "2", "6"),
        new("5 + 6 · cropped main + cropped tele", "5", "6"),
        new("3 + 4 · ultrawide + full tele", "3", "4")
    ];

    private static readonly ResolutionOption[] Resolutions =
    [
        new("1920×1080 · compatibility test", 1920, 1080),
        new("3840×2160 · dual 4K test", 3840, 2160)
    ];

    private readonly DualCameraPreview _preview;
    private readonly Picker _pairPicker;
    private readonly Picker _resolutionPicker;
    private readonly Label _statusLabel;

    public DualCameraPreviewPage()
    {
        Title = "Dual rear camera test";
        BackgroundColor = Color.FromArgb("#0B0D10");

        _pairPicker = new Picker
        {
            Title = "Physical camera pair",
            ItemsSource = CameraPairs,
            ItemDisplayBinding = new Binding(nameof(CameraPairOption.Label)),
            SelectedIndex = 0,
            TextColor = Colors.White
        };
        _resolutionPicker = new Picker
        {
            Title = "Requested preview resolution",
            ItemsSource = Resolutions,
            ItemDisplayBinding = new Binding(nameof(ResolutionOption.Label)),
            SelectedIndex = 0,
            TextColor = Colors.White
        };

        _statusLabel = new Label
        {
            Text = "Choose a pair and tap Start. Any CameraX rejection will appear here.",
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
                        Text = "Two physical lenses, one logical rear camera",
                        FontSize = 21,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White
                    },
                    new Label
                    {
                        Text = "This is a technology test. Both panels must show live, different fields of view for the pair to count as working.",
                        FontSize = 13,
                        TextColor = Color.FromArgb("#C8CFD7")
                    },
                    _pairPicker,
                    _resolutionPicker,
                    buttons,
                    new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition(GridLength.Star),
                            new ColumnDefinition(GridLength.Star)
                        },
                        Children =
                        {
                            CreatePreviewLabel("LEFT · first ID"),
                            CreatePreviewLabel("RIGHT · second ID", 1)
                        }
                    },
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
                        Text = "Try 2 + 4 first. If 4K fails, retry the same pair at 1080p. Then compare 5 + 4 with 2 + 4 to see whether ID 5 is a cropped main-sensor mode.",
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

        if (_pairPicker.SelectedItem is not CameraPairOption pair ||
            _resolutionPicker.SelectedItem is not ResolutionOption resolution)
        {
            _statusLabel.Text = "Select both a camera pair and a resolution.";
            return;
        }

        _statusLabel.Text = $"Binding physical IDs {pair.PrimaryId} + {pair.SecondaryId} at {resolution.Width}×{resolution.Height}…";
        _preview.Start(pair.PrimaryId, pair.SecondaryId, resolution.Width, resolution.Height);
    }

    private static Label CreatePreviewLabel(string text, int column = 0)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
            TextColor = Color.FromArgb("#F5B942")
        };
        Grid.SetColumn(label, column);
        return label;
    }

    private sealed record CameraPairOption(string Label, string PrimaryId, string SecondaryId)
    {
        public override string ToString() => Label;
    }

    private sealed record ResolutionOption(string Label, int Width, int Height)
    {
        public override string ToString() => Label;
    }
}
