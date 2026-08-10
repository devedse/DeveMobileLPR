using System.Text.Encodings.Web;
using System.Text.Json;
using DeveMobileLPR.App.ViewModels;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace DeveMobileLPR.App.Controls;

internal sealed class HistoryMapView : ContentView
{
    public static readonly BindableProperty MapProperty = BindableProperty.Create(
        nameof(Map),
        typeof(HistoryMapViewModel),
        typeof(HistoryMapView),
        propertyChanged: MapChanged);

    public static readonly BindableProperty IsInteractiveProperty = BindableProperty.Create(
        nameof(IsInteractive),
        typeof(bool),
        typeof(HistoryMapView),
        true,
        propertyChanged: IsInteractiveChanged);

    private const string TileUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    private const string Attribution = "&copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a> contributors";
    private const string MapUserAgent = "DeveMobileLPR/0.1 (+https://github.com/devedse/DeveMobileLPR)";
    private const int ThumbnailWidth = 144;
    private const int ThumbnailHeight = 96;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HybridWebView _webView;
    private readonly ActivityIndicator _loading;
    private readonly Label _fallback;
    private readonly Button _previewButton;
    private readonly Border _previewHint;
    private readonly Grid _root;
    private CancellationTokenSource? _renderCancellation;
    private string? _pendingRenderMessage;
    private bool _webContentReady;

    public HistoryMapView()
    {
        _webView = new HybridWebView
        {
            AutomationId = "HistoryMap.WebView",
            HybridRoot = "wwwroot",
            DefaultFile = "map/index.html",
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            ZIndex = 0
        };
        _webView.RawMessageReceived += RawMessageReceived;
        _webView.HandlerChanging += (_, _) => _webContentReady = false;
        _webView.HandlerChanged += (_, _) =>
        {
            ConfigurePlatformWebView();
            UpdateInteractionState();
        };

        _loading = new ActivityIndicator
        {
            IsRunning = true,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        _fallback = new Label
        {
            IsVisible = false,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            Margin = 24,
            TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#747E8E")
        };

        _previewButton = new Button
        {
            AutomationId = "HistoryMap.OpenButton",
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            IsVisible = false,
            Text = string.Empty,
            ZIndex = 10
        };
        SemanticProperties.SetDescription(_previewButton, "Open the interactive trip map");
        _previewButton.Clicked += (_, _) => RequestMap();

        _previewHint = new Border
        {
            IsVisible = false,
            InputTransparent = true,
            Margin = 12,
            Padding = new Thickness(12, 8),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.End,
            ZIndex = 11,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 9 },
            Content = new Label
            {
                Text = "Tap to open full map",
                FontAttributes = FontAttributes.Bold,
                FontSize = 13,
                TextColor = Colors.White
            }
        };
        _previewHint.BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#E6151922");

        _root = new Grid
        {
            Children = { _webView, _loading, _fallback, _previewButton, _previewHint }
        };
        var previewTap = new TapGestureRecognizer();
        previewTap.Tapped += (_, _) => RequestMap();
        _root.GestureRecognizers.Add(previewTap);
        Content = _root;
    }

    public event EventHandler<string>? VehicleSelected;
    public event EventHandler? MapRequested;

    public HistoryMapViewModel? Map
    {
        get => (HistoryMapViewModel?)GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public bool IsInteractive
    {
        get => (bool)GetValue(IsInteractiveProperty);
        set => SetValue(IsInteractiveProperty, value);
    }

    protected override void OnParentSet()
    {
        if (Parent is null)
        {
            _renderCancellation?.Cancel();
        }

        base.OnParentSet();
    }

    private static void MapChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((HistoryMapView)bindable).StartRender((HistoryMapViewModel?)newValue);

    private static void IsInteractiveChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var map = (HistoryMapView)bindable;
        map.UpdateInteractionState();
        map.StartRender(map.Map);
    }

    private void UpdateInteractionState()
    {
        _webView.InputTransparent = !IsInteractive;
        _previewButton.IsVisible = !IsInteractive && Map is not null && !_fallback.IsVisible;
        _previewHint.IsVisible = !IsInteractive && Map is not null && !_fallback.IsVisible;
    }

    private void RequestMap()
    {
        if (!IsInteractive && Map is not null)
        {
            MapRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StartRender(HistoryMapViewModel? map)
    {
        _renderCancellation?.Cancel();
        _renderCancellation?.Dispose();
        _renderCancellation = new CancellationTokenSource();
        _ = RenderAsync(map, _renderCancellation.Token);
    }

    private async Task RenderAsync(HistoryMapViewModel? map, CancellationToken cancellationToken)
    {
        _loading.IsVisible = true;
        _loading.IsRunning = true;
        _fallback.IsVisible = false;
        _webView.IsVisible = true;
        UpdateInteractionState();

        if (map is null || (map.Route.Count == 0 && map.Sightings.Count == 0))
        {
            ShowFallback("No route or located vehicle sightings were recorded for this drive.");
            return;
        }

        try
        {
            var sightings = new List<MapSighting>(map.Sightings.Count);
            foreach (var sighting in map.Sightings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sightings.Add(new MapSighting(
                    sighting.NormalizedPlate,
                    sighting.DisplayPlate,
                    sighting.Price,
                    sighting.IsKnown,
                    sighting.Seen,
                    $"{sighting.Confidence:P0} · {sighting.ObservationCount} reads",
                    sighting.VehicleName,
                    sighting.Location.Latitude,
                    sighting.Location.Longitude,
                    sighting.Location.AccuracyMeters,
                    await CreateThumbnailDataUrlAsync(sighting.SnapshotPath, cancellationToken).ConfigureAwait(false)));
            }

            var payload = new MapPayload(
                map.Route.Select(point => new[] { point.Latitude, point.Longitude }).ToArray(),
                sightings,
                TileUrl,
                Attribution,
                IsInteractive,
                map.CanOpenVehicleHistory);
            var message = JsonSerializer.Serialize(new MapHostMessage("render", payload), JsonOptions);

            await Dispatcher.DispatchAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _pendingRenderMessage = message;
                SendPendingRender();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.DispatchAsync(() => ShowFallback($"The map could not be loaded. {exception.Message}"));
        }
    }

    private void RawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Message))
        {
            return;
        }

        try
        {
            using var message = JsonDocument.Parse(args.Message);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "web-ready":
                    Dispatcher.Dispatch(() =>
                    {
                        _webContentReady = true;
                        SendPendingRender();
                    });
                    break;
                case "map-ready":
                    Dispatcher.Dispatch(() =>
                    {
                        _loading.IsRunning = false;
                        _loading.IsVisible = false;
                    });
                    break;
                case "vehicle" when root.TryGetProperty("plate", out var plateElement):
                    var plate = plateElement.GetString();
                    if (!string.IsNullOrWhiteSpace(plate))
                    {
                        Dispatcher.Dispatch(() => VehicleSelected?.Invoke(this, plate));
                    }
                    break;
                case "error" when root.TryGetProperty("message", out var errorElement):
                    var error = errorElement.GetString();
                    Dispatcher.Dispatch(() => ShowFallback($"The map could not be loaded. {error}"));
                    break;
            }
        }
        catch (JsonException)
        {
            Dispatcher.Dispatch(() => ShowFallback("The map could not be loaded because it returned an invalid response."));
        }
    }

    private void SendPendingRender()
    {
        if (!_webContentReady || _pendingRenderMessage is null || _webView.Handler is null)
        {
            return;
        }

        try
        {
            _webView.SendRawMessage(_pendingRenderMessage);
        }
        catch (InvalidOperationException)
        {
            _webContentReady = false;
        }
    }

    private void ConfigurePlatformWebView()
    {
    #if ANDROID
        if (_webView.Handler?.PlatformView is global::Android.Webkit.WebView androidView)
        {
            var existing = androidView.Settings.UserAgentString ?? string.Empty;
            if (!existing.StartsWith(MapUserAgent, StringComparison.Ordinal))
            {
                androidView.Settings.UserAgentString = $"{MapUserAgent} {existing}";
            }
        }
    #elif WINDOWS
        if (_webView.Handler?.PlatformView is Microsoft.Maui.Platform.MauiHybridWebView windowsView)
        {
            windowsView.RunAfterInitialize(() =>
            {
                var settings = windowsView.CoreWebView2?.Settings;
                if (settings is null || settings.UserAgent.StartsWith(MapUserAgent, StringComparison.Ordinal))
                {
                    return;
                }

                settings.UserAgent = $"{MapUserAgent} {settings.UserAgent}";
            });
        }
    #endif
    }

    private void ShowFallback(string message)
    {
        _pendingRenderMessage = null;
        _loading.IsRunning = false;
        _loading.IsVisible = false;
        _webView.IsVisible = false;
        _previewButton.IsVisible = false;
        _previewHint.IsVisible = false;
        _fallback.Text = message;
        _fallback.IsVisible = true;
    }

    private static async Task<string?> CreateThumbnailDataUrlAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var image = await SixLabors.ImageSharp.Image.LoadAsync(path, cancellationToken).ConfigureAwait(false);
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(ThumbnailWidth, ThumbnailHeight),
                Mode = SixLabors.ImageSharp.Processing.ResizeMode.Crop,
                Position = AnchorPositionMode.Center
            }));
            await using var output = new MemoryStream();
            await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 72 }, cancellationToken).ConfigureAwait(false);
            return $"data:image/jpeg;base64,{Convert.ToBase64String(output.ToArray())}";
        }
        catch (UnknownImageFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed record MapHostMessage(string Type, MapPayload Payload);
    private sealed record MapPayload(
        IReadOnlyList<double[]> Route,
        IReadOnlyList<MapSighting> Sightings,
        string TileUrl,
        string Attribution,
        bool IsInteractive,
        bool CanOpenVehicleHistory);
    private sealed record MapSighting(
        string NormalizedPlate,
        string DisplayPlate,
        string? Price,
        bool IsKnown,
        string Seen,
        string Confidence,
        string VehicleName,
        double Latitude,
        double Longitude,
        float? AccuracyMeters,
        string? Image);
}
