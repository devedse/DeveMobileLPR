using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Camera.View;
using AndroidX.Lifecycle;
using DeveMobileLPR.Application;
using DeveMobileLPR.Imaging;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class AndroidDriveVideoInput : IDriveVideoInput
{
    private readonly Context _context;
    private readonly IDriveSourceCatalog _sourceCatalog;
    private readonly LinearLayout _previewGrid;
    private readonly AndroidVideoTextureView _networkPreview;
    private readonly CameraXIntegratedFrameSource _integrated;
    private readonly Camera2PhysicalFrameSource _physicalPair;
    private readonly AndroidHlsFrameSource _network;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<string, PreviewView> _cameraPreviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextureView> _camera2Previews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextView> _sourceStatusLabels = new(StringComparer.Ordinal);
    private IReadOnlyList<DriveSourceCapability> _sourceCapabilities = [];
    private IReadOnlyList<CameraChoice> _cameraChoices = [new("rear", "Rear cameras - automatic lens")];
    private DriveInputConfiguration _configuration = DriveInputConfiguration.Default;
    private string _selectedCameraId = "rear";
    private bool _running;
    private bool _disposed;

    public AndroidDriveVideoInput(
        Context context,
        ILifecycleOwner lifecycleOwner,
        IDriveSourceCatalog sourceCatalog,
        LinearLayout previewGrid,
        AndroidVideoTextureView networkPreview,
        string networkStreamUrl,
        Func<int> recognitionFramesPerSecond,
        Func<string, bool> hasPendingRecognitionFrame,
        Func<string, Yuv420Frame, bool> submitFrame)
    {
        _context = context;
        _sourceCatalog = sourceCatalog;
        _previewGrid = previewGrid;
        _networkPreview = networkPreview;
        _integrated = new CameraXIntegratedFrameSource(
            context,
            lifecycleOwner,
            recognitionFramesPerSecond,
            submitFrame);
        _physicalPair = new Camera2PhysicalFrameSource(
            context,
            recognitionFramesPerSecond,
            submitFrame);
        _network = new AndroidHlsFrameSource(
            context,
            networkPreview,
            networkStreamUrl,
            recognitionFramesPerSecond,
            () => hasPendingRecognitionFrame(DriveInputIds.NetworkLlHls),
            frame => submitFrame(DriveInputIds.NetworkLlHls, frame));

        _integrated.Diagnostic += IntegratedDiagnostic;
        _integrated.SourceFramesAvailable += ChildSourceFramesAvailable;
        _integrated.SourceStatusChanged += PhysicalSourceStatusChanged;
        _physicalPair.Diagnostic += IntegratedDiagnostic;
        _physicalPair.SourceFramesAvailable += ChildSourceFramesAvailable;
        _physicalPair.SourceStatusChanged += PhysicalSourceStatusChanged;
        _network.Diagnostic += ChildDiagnostic;
        _network.SourceFramesAvailable += ChildSourceFramesAvailable;
        _network.PreviewFramesPresented += ChildPreviewFramesPresented;
    }

    public event EventHandler<DriveInputDiagnostic>? Diagnostic;
    public event EventHandler<IReadOnlyList<CameraChoice>>? CameraChoicesChanged;
    public event EventHandler<DriveFrameCountEventArgs>? SourceFramesAvailable;
    public event EventHandler<DriveFrameCountEventArgs>? PreviewFramesPresented;

    public IReadOnlyList<CameraChoice> CameraChoices => _cameraChoices;
    public IReadOnlyList<DriveSourceCapability> SourceCapabilities => _sourceCapabilities;
    public string SelectedCameraId => _selectedCameraId;
    public bool ReportsPreviewFrames =>
        _configuration.EnabledSources.Any(source => source.SourceId == DriveInputIds.NetworkLlHls);
    public bool SupportsNetworkStreams => true;
    public bool IsReady => _configuration.EnabledSources.Count > 0
        && (_configuration.EnabledSources.All(source => source.SourceId != DriveInputIds.NetworkLlHls)
            || _network.IsReady);

    public async Task InitializeAsync(string preferredCameraId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sourceCapabilities = await new AndroidDriveSourceCatalog(_context)
            .DiscoverAsync(cancellationToken)
            .ConfigureAwait(false);
        _cameraChoices = _sourceCapabilities
            .Where(source => source.Id is "rear" or "front" || source.Kind == DriveSourceKind.NetworkLlHls)
            .Select(source => new CameraChoice(source.Id, source.Name))
            .ToArray();
        CameraChoicesChanged?.Invoke(this, _cameraChoices);
        await _integrated.PrepareAsync(cancellationToken).ConfigureAwait(false);

        var preferred = _sourceCapabilities.FirstOrDefault(source => source.Id == preferredCameraId)
            ?? _sourceCapabilities.First(source => source.Id == "rear");
        var profile = CreateDefaultProfile(preferred);
        await ApplyConfigurationAsync(
            new DriveInputConfiguration(
                DriveInputConfiguration.CurrentVersion,
                DriveInputMode.Single,
                [profile],
                profile.SourceId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyConfigurationAsync(
        DriveInputConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var enabled = configuration.EnabledSources;
        if (enabled.Count == 0)
        {
            throw new InvalidOperationException("Enable at least one video source.");
        }

        var integratedCount = enabled.Count(source =>
            FindCapability(source.SourceId)?.IsIntegratedCamera == true);
        if (integratedCount > 2)
        {
            throw new NotSupportedException(
                "Android supports at most two simultaneous integrated camera streams.");
        }
        if (configuration.Mode == DriveInputMode.Single && enabled.Count != 1)
        {
            throw new InvalidOperationException("Single-camera mode requires exactly one source.");
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var restart = _running;
            if (restart)
            {
                await StopCoreAsync().ConfigureAwait(false);
            }

            _configuration = NormalizeConfiguration(configuration);
            _selectedCameraId = _configuration.Mode == DriveInputMode.Multi
                ? "multi"
                : _configuration.EnabledSources[0].SourceId;
            await MainThread.InvokeOnMainThreadAsync(BuildPreviewGrid);
            ConfigureIntegratedSource();
            ConfigureNetworkSource();

            if (restart)
            {
                await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running)
            {
                return;
            }

            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SelectCameraAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        var capability = FindCapability(cameraId)
            ?? throw new ArgumentException("The selected Android video input is unavailable.", nameof(cameraId));
        await ApplyConfigurationAsync(
            new DriveInputConfiguration(
                DriveInputConfiguration.CurrentVersion,
                DriveInputMode.Single,
                [CreateDefaultProfile(capability)],
                capability.Id),
            cancellationToken).ConfigureAwait(false);
    }

    public void SetZoom(float zoomRatio)
    {
        if (_configuration.Mode != DriveInputMode.Single)
        {
            return;
        }

        var source = _configuration.EnabledSources[0];
        _configuration = _configuration with
        {
            Sources =
            [
                source with { Zoom = zoomRatio }
            ]
        };
        _integrated.SetZoom(source.SourceId, zoomRatio);
    }

    public void SetNetworkStreamUrl(string value)
    {
        _network.SetNetworkStreamUrl(value);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        var integrated = _configuration.EnabledSources
            .Where(source => FindCapability(source.SourceId)?.IsIntegratedCamera == true)
            .ToArray();
        var networkEnabled = _configuration.EnabledSources
            .Any(source => source.SourceId == DriveInputIds.NetworkLlHls);

        if (integrated.Length > 0)
        {
            if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
            {
                throw new UnauthorizedAccessException(
                    "Camera access is required to recognize plates.");
            }

            if (UsesCamera2PhysicalPair)
            {
                await _physicalPair.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _integrated.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            if (networkEnabled)
            {
                await _network.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            _running = true;
        }
        catch
        {
            _integrated.Stop();
            _physicalPair.Stop();
            throw;
        }
    }

    private async Task StopCoreAsync()
    {
        _running = false;
        _integrated.Stop();
        _physicalPair.Stop();
        await _network.StopAsync().ConfigureAwait(false);
    }

    private void ConfigureIntegratedSource()
    {
        var sources = _configuration.EnabledSources
            .Select(profile => (Capability: FindCapability(profile.SourceId), Profile: profile))
            .Where(item => item.Capability?.IsIntegratedCamera == true)
            .Select(item => (Capability: item.Capability!, item.Profile))
            .ToArray();
        if (sources.Length == 0)
        {
            return;
        }

        if (UsesCamera2PhysicalPair)
        {
            _physicalPair.Configure(sources.Select(source => (
                source.Capability,
                source.Profile,
                _camera2Previews[source.Profile.SourceId])).ToArray());
        }
        else
        {
            _integrated.Configure(sources.Select(source => (
                source.Capability,
                source.Profile,
                _cameraPreviews[source.Profile.SourceId])).ToArray());
        }
    }

    private bool UsesCamera2PhysicalPair
    {
        get
        {
            var integrated = _configuration.EnabledSources
                .Select(source => FindCapability(source.SourceId))
                .Where(source => source?.IsIntegratedCamera == true)
                .ToArray();
            return integrated.Length == 2
                && integrated.All(source => source!.Kind == DriveSourceKind.PhysicalCamera)
                && integrated.Select(source => source!.LogicalCameraId).Distinct().Count() == 1;
        }
    }

    private void ConfigureNetworkSource()
    {
        var profile = _configuration.EnabledSources
            .FirstOrDefault(source => source.SourceId == DriveInputIds.NetworkLlHls);
        if (profile?.NetworkUrl is not null)
        {
            _network.SetNetworkStreamUrl(profile.NetworkUrl);
        }
    }

    private void BuildPreviewGrid()
    {
        _previewGrid.RemoveAllViews();
        _cameraPreviews.Clear();
        _camera2Previews.Clear();
        _sourceStatusLabels.Clear();
        var sources = _configuration.EnabledSources;
        var columns = sources.Count == 1 ? 1 : 2;
        var rows = (sources.Count + columns - 1) / columns;
        for (var rowIndex = 0; rowIndex < rows; rowIndex++)
        {
            var row = new LinearLayout(_context) { Orientation = Orientation.Horizontal };
            _previewGrid.AddView(
                row,
                new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
            for (var column = 0; column < columns; column++)
            {
                var index = rowIndex * columns + column;
                if (index >= sources.Count)
                {
                    row.AddView(
                        new Space(_context),
                        new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
                    continue;
                }

                var profile = sources[index];
                global::Android.Views.View preview;
                if (profile.SourceId == DriveInputIds.NetworkLlHls)
                {
                    preview = _networkPreview;
                }
                else if (UsesCamera2PhysicalPair)
                {
                    var texturePreview = new TextureView(_context);
                    _camera2Previews.Add(profile.SourceId, texturePreview);
                    preview = texturePreview;
                }
                else
                {
                    var cameraPreview = new PreviewView(_context);
                    cameraPreview.SetImplementationMode(PreviewView.ImplementationMode.Compatible);
                    cameraPreview.SetScaleType(sources.Count == 1
                        ? PreviewView.ScaleType.FillCenter
                        : PreviewView.ScaleType.FitCenter);
                    _cameraPreviews.Add(profile.SourceId, cameraPreview);
                    preview = cameraPreview;
                }

                if (preview.Parent is ViewGroup parent)
                {
                    parent.RemoveView(preview);
                }
                var panel = new FrameLayout(_context);
                panel.AddView(preview, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent));
                var capability = FindCapability(profile.SourceId);
                var label = new TextView(_context)
                {
                    Text = $"{capability?.Name ?? profile.SourceId}\nWAITING",
                    TextSize = 10,
                    Gravity = GravityFlags.Left
                };
                label.SetTextColor(global::Android.Graphics.Color.White);
                label.SetBackgroundColor(global::Android.Graphics.Color.Argb(190, 10, 13, 18));
                label.SetPadding(12, 8, 12, 8);
                panel.AddView(label, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent,
                    ViewGroup.LayoutParams.WrapContent,
                    GravityFlags.Top | GravityFlags.Left));
                _sourceStatusLabels[profile.SourceId] = label;
                row.AddView(panel, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
            }
        }
    }

    private DriveInputConfiguration NormalizeConfiguration(DriveInputConfiguration configuration)
    {
        var profiles = configuration.Sources.Select(profile =>
        {
            var capability = FindCapability(profile.SourceId);
            if (capability is null)
            {
                return profile with { Enabled = false };
            }

            var resolution = capability.Kind == DriveSourceKind.NetworkLlHls
                ? profile.Resolution
                : SelectResolution(capability.Resolutions, profile.Resolution);
            return profile with
            {
                Resolution = resolution,
                Zoom = Math.Clamp(profile.Zoom, capability.MinimumZoom, capability.MaximumZoom),
                NetworkUrl = profile.NetworkUrl?.Trim()
            };
        }).ToArray();
        return configuration with { Sources = profiles };
    }

    private DriveSourceCapability? FindCapability(string sourceId) =>
        _sourceCapabilities.FirstOrDefault(source => source.Id == sourceId);

    private static DriveSourceProfile CreateDefaultProfile(DriveSourceCapability capability) =>
        new(
            capability.Id,
            true,
            SelectResolution(capability.Resolutions, new VideoResolution(3840, 2160)),
            1f,
            capability.Kind == DriveSourceKind.NetworkLlHls ? string.Empty : null);

    private static VideoResolution SelectResolution(
        IReadOnlyList<VideoResolution> available,
        VideoResolution requested)
    {
        if (available.Count == 0)
        {
            return requested;
        }

        return available.FirstOrDefault(size => size == requested)
            ?? available
                .OrderBy(size => size.Width >= requested.Width && size.Height >= requested.Height ? 0 : 1)
                .ThenBy(size => Math.Abs(size.PixelCount - requested.PixelCount))
                .First();
    }

    private void IntegratedDiagnostic(object? sender, string message) =>
        Diagnostic?.Invoke(this, new DriveInputDiagnostic(
            message,
            message.Contains("failed", StringComparison.OrdinalIgnoreCase)));

    private void ChildDiagnostic(object? sender, string message) =>
        Diagnostic?.Invoke(this, new DriveInputDiagnostic(
            message,
            message.StartsWith("Could not", StringComparison.Ordinal)
                || message.Contains("failed", StringComparison.OrdinalIgnoreCase)));

    private void ChildSourceFramesAvailable(object? sender, DriveFrameCountEventArgs args) =>
        SourceFramesAvailable?.Invoke(this, args);

    private void ChildPreviewFramesPresented(object? sender, DriveFrameCountEventArgs args) =>
        PreviewFramesPresented?.Invoke(this, args);

    private void PhysicalSourceStatusChanged(string sourceId, string status, bool isError)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_sourceStatusLabels.TryGetValue(sourceId, out var label))
            {
                var name = FindCapability(sourceId)?.Name ?? sourceId;
                label.Text = $"{name}\n{status}";
                label.SetTextColor(isError
                    ? global::Android.Graphics.Color.Rgb(255, 141, 141)
                    : global::Android.Graphics.Color.White);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            _integrated.Diagnostic -= IntegratedDiagnostic;
            _integrated.SourceFramesAvailable -= ChildSourceFramesAvailable;
            _integrated.SourceStatusChanged -= PhysicalSourceStatusChanged;
            _physicalPair.Diagnostic -= IntegratedDiagnostic;
            _physicalPair.SourceFramesAvailable -= ChildSourceFramesAvailable;
            _physicalPair.SourceStatusChanged -= PhysicalSourceStatusChanged;
            _network.Diagnostic -= ChildDiagnostic;
            _network.SourceFramesAvailable -= ChildSourceFramesAvailable;
            _network.PreviewFramesPresented -= ChildPreviewFramesPresented;
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _integrated.Dispose();
                _physicalPair.Dispose();
                _network.Dispose();
            });
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }
}
