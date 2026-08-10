using System.Text;
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
    private const int ThumbnailWidth = 144;
    private const int ThumbnailHeight = 96;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly SemaphoreSlim AssetGate = new(1, 1);
    private static MapAssets? _assets;

    private readonly WebView _webView;
    private readonly ActivityIndicator _loading;
    private readonly Label _fallback;
    private readonly Button _previewButton;
    private readonly Border _previewHint;
    private readonly Grid _root;
    private CancellationTokenSource? _renderCancellation;

    public HistoryMapView()
    {
        _webView = new WebView
        {
            AutomationId = "HistoryMap.WebView",
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            ZIndex = 0
        };
        _webView.Navigating += Navigating;
        _webView.HandlerChanged += (_, _) => UpdateInteractionState();

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
        _previewButton.IsVisible = !IsInteractive && Map is not null;
        _previewHint.IsVisible = !IsInteractive && Map is not null;
    }

    private void RequestMap()
    {
        if (!IsInteractive && Map is not null) MapRequested?.Invoke(this, EventArgs.Empty);
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
            var assets = await LoadAssetsAsync(cancellationToken).ConfigureAwait(false);
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
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            var html = BuildHtml(assets, encodedPayload);

            await Dispatcher.DispatchAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                _webView.Source = new HtmlWebViewSource { Html = html };
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

    private void Navigating(object? sender, WebNavigatingEventArgs args)
    {
        if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("app", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        args.Cancel = true;
        if (uri.Host.Equals("map-ready", StringComparison.OrdinalIgnoreCase))
        {
            _loading.IsRunning = false;
            _loading.IsVisible = false;
            return;
        }

        if (uri.Host.Equals("vehicle", StringComparison.OrdinalIgnoreCase))
        {
            var plate = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
            if (!string.IsNullOrWhiteSpace(plate)) VehicleSelected?.Invoke(this, plate);
        }
    }

    private void ShowFallback(string message)
    {
        _loading.IsRunning = false;
        _loading.IsVisible = false;
        _webView.IsVisible = false;
        _previewButton.IsVisible = false;
        _previewHint.IsVisible = false;
        _fallback.Text = message;
        _fallback.IsVisible = true;
    }

    private static async Task<MapAssets> LoadAssetsAsync(CancellationToken cancellationToken)
    {
        if (_assets is not null) return _assets;
        await AssetGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_assets is not null) return _assets;
            _assets = new MapAssets(
                await ReadAssetAsync("map/leaflet.css", cancellationToken).ConfigureAwait(false),
                await ReadAssetAsync("map/MarkerCluster.css", cancellationToken).ConfigureAwait(false),
                await ReadAssetAsync("map/MarkerCluster.Default.css", cancellationToken).ConfigureAwait(false),
                await ReadAssetAsync("map/leaflet.js", cancellationToken).ConfigureAwait(false),
                await ReadAssetAsync("map/leaflet.markercluster.js", cancellationToken).ConfigureAwait(false));
            return _assets;
        }
        finally
        {
            AssetGate.Release();
        }
    }

    private static async Task<string> ReadAssetAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await FileSystem.Current.OpenAppPackageFileAsync(path).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> CreateThumbnailDataUrlAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path)) return null;
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

    private static string BuildHtml(MapAssets assets, string encodedPayload) => $$"""
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
          <meta name="referrer" content="strict-origin-when-cross-origin">
          <style>{{assets.LeafletCss}}</style>
          <style>{{assets.ClusterCss}}</style>
          <style>{{assets.ClusterDefaultCss}}</style>
          <style>
            html, body, #map { width:100%; height:100%; margin:0; background:#202632; }
            .leaflet-container { font-family:system-ui,-apple-system,"Segoe UI",sans-serif; background:#202632; }
            .leaflet-control-attribution { font-size:10px; }
            .photo-pin { --pin-accent:#f5c542; width:58px; height:52px; }
            .photo-pin--known { --pin-accent:#d77bff; }
            .photo-pin__image, .photo-pin__fallback { width:54px; height:36px; border:3px solid var(--pin-accent); border-radius:9px; box-shadow:0 3px 10px #0008; background:#151922; object-fit:cover; display:flex; align-items:center; justify-content:center; color:var(--pin-accent); font-size:10px; font-weight:800; }
            .photo-pin__plate { position:absolute; top:36px; left:50%; transform:translateX(-50%); white-space:nowrap; padding:2px 5px; border-radius:4px; background:var(--pin-accent); color:#151922; box-shadow:0 2px 6px #0007; font-size:9px; font-weight:900; }
            .photo-pin__price { position:absolute; top:-7px; right:-10px; white-space:nowrap; padding:3px 6px; border:2px solid #151922; border-radius:8px; background:var(--pin-accent); color:#151922; box-shadow:0 2px 7px #0008; font-size:9px; font-weight:900; }
            .endpoint { width:18px; height:18px; border:3px solid #151922; border-radius:50%; box-shadow:0 2px 8px #0008; }
            .endpoint--start { background:#f5c542; }
            .endpoint--finish { background:#58e0c2; }
            .leaflet-popup-content-wrapper { max-height:calc(100dvh - 72px); overflow:hidden; }
            .leaflet-popup-content { max-height:calc(100dvh - 112px); overflow-y:auto; overscroll-behavior:contain; }
            .popup { min-width:min(190px, calc(100vw - 96px)); color:#151922; }
            .popup img { width:100%; max-height:120px; object-fit:cover; border-radius:8px; margin-bottom:8px; }
            .popup__plate { font-size:17px; font-weight:900; }
            .popup__meta { color:#596273; margin-top:3px; }
            .popup button { margin-top:10px; width:100%; border:0; border-radius:7px; padding:8px; background:#151922; color:#fff; font-weight:700; }
            #tile-warning { display:none; position:absolute; z-index:1000; left:12px; right:12px; top:12px; padding:9px 12px; border-radius:8px; background:#151922e8; color:#fff; font-size:12px; text-align:center; box-shadow:0 3px 12px #0007; }
            .marker-cluster div { color:#151922; font-weight:900; }
            .marker-cluster--new, .marker-cluster--new div { background:#f5c542; }
            .marker-cluster--new { background:#f5c54266; }
            .marker-cluster--known, .marker-cluster--known div { background:#d77bff; }
            .marker-cluster--known { background:#d77bff66; }
            .marker-cluster--mixed { background:linear-gradient(90deg,#f5c54266 0 50%,#d77bff66 50% 100%); }
            .marker-cluster--mixed div { background:linear-gradient(90deg,#f5c542 0 50%,#d77bff 50% 100%); }
          </style>
        </head>
        <body>
          <div id="map"></div><div id="tile-warning">The street map is unavailable. Your recorded route and sightings are still shown.</div>
          <script>{{assets.LeafletJs}}</script>
          <script>{{assets.ClusterJs}}</script>
          <script>
          (() => {
            const payload = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('{{encodedPayload}}'), c => c.charCodeAt(0))));
            const interactive = payload.isInteractive;
            const map = L.map('map', {
              zoomControl:interactive, preferCanvas:true, attributionControl:true,
              dragging:interactive, scrollWheelZoom:interactive, doubleClickZoom:interactive,
              boxZoom:interactive, keyboard:interactive, touchZoom:interactive
            });
            let tileErrors = 0;
            L.tileLayer(payload.tileUrl, { maxZoom:19, attribution:payload.attribution, crossOrigin:true, updateWhenIdle:true, keepBuffer:2 })
              .on('tileerror', () => { if (++tileErrors === 3) document.getElementById('tile-warning').style.display='block'; })
              .addTo(map);
            const bounds = [];
            if (payload.route.length) {
              payload.route.forEach(p => bounds.push(p));
              L.polyline(payload.route, { color:'#20b99a', weight:6, opacity:.95, lineCap:'round', lineJoin:'round' }).addTo(map);
              const endpoint = (kind) => L.divIcon({ className:'', html:`<div class="endpoint endpoint--${kind}"></div>`, iconSize:[24,24], iconAnchor:[12,12] });
              const start=L.marker(payload.route[0], { icon:endpoint('start'), zIndexOffset:500 }).addTo(map);
              const finish=L.marker(payload.route[payload.route.length-1], { icon:endpoint('finish'), zIndexOffset:500 }).addTo(map);
              if (interactive) { start.bindTooltip('Trip start'); finish.bindTooltip('Trip finish'); }
            }
            const clusterIcon = cluster => {
              const count=cluster.getChildCount();
              const knownCount=cluster.getAllChildMarkers().filter(marker => marker.options.isKnown).length;
              const tone=knownCount === 0 ? 'new' : knownCount === count ? 'known' : 'mixed';
              const size=count < 10 ? 'small' : count < 100 ? 'medium' : 'large';
              const diameter=size === 'small' ? 40 : size === 'medium' ? 50 : 60;
              return L.divIcon({
                html:`<div><span>${count}</span></div>`,
                className:`marker-cluster marker-cluster-${size} marker-cluster--${tone}`,
                iconSize:L.point(diameter,diameter)
              });
            };
            const clusters = L.markerClusterGroup({ showCoverageOnHover:false, maxClusterRadius:52, spiderfyOnMaxZoom:interactive, removeOutsideVisibleBounds:false, iconCreateFunction:clusterIcon });
            payload.sightings.forEach(s => {
              const root = document.createElement('div'); root.className=`photo-pin${s.isKnown ? ' photo-pin--known' : ''}`;
              if (s.image) { const img=document.createElement('img'); img.className='photo-pin__image'; img.src=s.image; img.alt=''; root.appendChild(img); }
              else { const fallback=document.createElement('div'); fallback.className='photo-pin__fallback'; fallback.textContent='CAR'; root.appendChild(fallback); }
              const label=document.createElement('div'); label.className='photo-pin__plate'; label.textContent=s.displayPlate; root.appendChild(label);
              if (s.price) { const price=document.createElement('div'); price.className='photo-pin__price'; price.textContent=s.price; root.appendChild(price); }
              const marker=L.marker([s.latitude,s.longitude], { isKnown:s.isKnown, icon:L.divIcon({ className:'', html:root, iconSize:[58,52], iconAnchor:[29,45], popupAnchor:[0,-44] }) });
              const popup=document.createElement('div'); popup.className='popup';
              if (s.image) { const image=document.createElement('img'); image.src=s.image; image.alt='Vehicle snapshot'; popup.appendChild(image); }
              const plate=document.createElement('div'); plate.className='popup__plate'; plate.textContent=s.displayPlate; popup.appendChild(plate);
              [s.vehicleName, s.seen, s.confidence, s.accuracyMeters == null ? null : `GPS accuracy ±${Math.round(s.accuracyMeters)} m`].filter(Boolean).forEach(value => { const row=document.createElement('div'); row.className='popup__meta'; row.textContent=value; popup.appendChild(row); });
              if (payload.canOpenVehicleHistory) { const button=document.createElement('button'); button.type='button'; button.textContent='View vehicle history'; button.onclick=()=>location.href=`app://vehicle/${encodeURIComponent(s.normalizedPlate)}`; popup.appendChild(button); }
              if (interactive) marker.bindPopup(popup, { maxWidth:300, maxHeight:Math.max(120, map.getSize().y - 104), autoPan:true, keepInView:false, autoPanPadding:[16,16] });
              clusters.addLayer(marker); bounds.push([s.latitude,s.longitude]);
            });
            map.addLayer(clusters);
            if (bounds.length === 1) map.setView(bounds[0], 16); else map.fitBounds(bounds, { padding:[38,38], maxZoom:17 });
            setTimeout(() => { map.invalidateSize(); location.href='app://map-ready'; }, 0);
          })();
          </script>
        </body>
        </html>
        """;

    private sealed record MapAssets(string LeafletCss, string ClusterCss, string ClusterDefaultCss, string LeafletJs, string ClusterJs);
    private sealed record MapPayload(IReadOnlyList<double[]> Route, IReadOnlyList<MapSighting> Sightings, string TileUrl, string Attribution, bool IsInteractive, bool CanOpenVehicleHistory);
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
