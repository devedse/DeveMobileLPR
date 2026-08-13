using Android.Content;
using Android.Views;
using Android.Runtime;
using Android.Widget;
using AndroidX.Camera.Core;
using AndroidX.Camera.Core.ResolutionSelector;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using DeveMobileLPR.App.Controls;
using Google.Common.Util.Concurrent;
using Java.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using AndroidSize = Android.Util.Size;

namespace DeveMobileLPR.App.Platforms.Android.Camera;

internal sealed class DualCameraPreviewHandler : ViewHandler<DualCameraPreview, LinearLayout>
{
    public static readonly IPropertyMapper<DualCameraPreview, DualCameraPreviewHandler> Mapper =
        new PropertyMapper<DualCameraPreview, DualCameraPreviewHandler>(ViewHandler.ViewMapper)
        {
            [nameof(DualCameraPreview.IsActive)] = static (handler, view) => handler.UpdateActive(view.IsActive)
        };

    private PreviewView? _primaryView;
    private PreviewView? _secondaryView;
    private ProcessCameraProvider? _provider;
    private Preview? _primaryPreview;
    private Preview? _secondaryPreview;
    private int _operationVersion;

    public DualCameraPreviewHandler() : base(Mapper)
    {
    }

    protected override LinearLayout CreatePlatformView()
    {
        var context = MauiContext?.Context ?? throw new InvalidOperationException("Android context is unavailable.");
        var root = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal
        };
        _primaryView = CreatePreviewView(context);
        _secondaryView = CreatePreviewView(context);
        root.AddView(_primaryView, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
        root.AddView(_secondaryView, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1));
        return root;
    }

    protected override void ConnectHandler(LinearLayout platformView)
    {
        base.ConnectHandler(platformView);
        UpdateActive(VirtualView.IsActive);
    }

    protected override void DisconnectHandler(LinearLayout platformView)
    {
        Stop();
        _primaryView = null;
        _secondaryView = null;
        base.DisconnectHandler(platformView);
    }

    private static PreviewView CreatePreviewView(Context context)
    {
        var view = new PreviewView(context);
        view.SetImplementationMode(PreviewView.ImplementationMode.Compatible);
        view.SetScaleType(PreviewView.ScaleType.FitCenter);
        view.SetBackgroundColor(global::Android.Graphics.Color.Black);
        return view;
    }

    private void UpdateActive(bool active)
    {
        if (!active)
        {
            Stop();
            return;
        }

        var version = Interlocked.Increment(ref _operationVersion);
        _ = StartAsync(version);
    }

    private async Task StartAsync(int version)
    {
        try
        {
            VirtualView.ReportStatus(
                $"CameraX bind starting: physical {VirtualView.PrimaryCameraId} + {VirtualView.SecondaryCameraId}, " +
                $"{VirtualView.RequestedWidth}×{VirtualView.RequestedHeight} each.");

            var provider = await GetProviderAsync();
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (version != _operationVersion || !VirtualView.IsActive)
                {
                    return;
                }

                Bind(provider);
            });

            await Task.Delay(700);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (version != _operationVersion || !VirtualView.IsActive)
                {
                    return;
                }

                VirtualView.ReportStatus(BuildSuccessStatus());
            });
        }
        catch (Exception exception)
        {
            VirtualView.ReportStatus(
                $"BIND FAILED\n{exception.GetType().Name}: {exception.Message}\n\n" +
                "This pair/resolution is not accepted by CameraX on this device. Try 1080p or another pair.");
        }
    }

    private async Task<ProcessCameraProvider> GetProviderAsync()
    {
        if (_provider is not null)
        {
            return _provider;
        }

        var context = MauiContext?.Context ?? throw new InvalidOperationException("Android context is unavailable.");
        var completion = new TaskCompletionSource<ProcessCameraProvider>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var future = ProcessCameraProvider.GetInstance(context);
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            try
            {
                completion.TrySetResult(
                    (ProcessCameraProvider?)future.Get()
                    ?? throw new InvalidOperationException("CameraX returned no camera provider."));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }), ContextCompat.GetMainExecutor(context));
        _provider = await completion.Task;
        return _provider;
    }

    private void Bind(ProcessCameraProvider provider)
    {
        var primaryView = _primaryView ?? throw new InvalidOperationException("First preview surface is unavailable.");
        var secondaryView = _secondaryView ?? throw new InvalidOperationException("Second preview surface is unavailable.");
        var context = MauiContext?.Context ?? throw new InvalidOperationException("Android context is unavailable.");
        var lifecycleOwner = MauiContext?.Services.GetRequiredService<AndroidCameraLifecycleOwner>()
            ?? throw new InvalidOperationException("Camera lifecycle is unavailable.");

        provider.UnbindAll();

        var primarySelector = BuildPhysicalSelector(VirtualView.PrimaryCameraId);
        var secondarySelector = BuildPhysicalSelector(VirtualView.SecondaryCameraId);
        _primaryPreview = BuildPreview(VirtualView.RequestedWidth, VirtualView.RequestedHeight);
        _secondaryPreview = BuildPreview(VirtualView.RequestedWidth, VirtualView.RequestedHeight);
        _primaryPreview.SetSurfaceProvider(ContextCompat.GetMainExecutor(context), primaryView.SurfaceProvider);
        _secondaryPreview.SetSurfaceProvider(ContextCompat.GetMainExecutor(context), secondaryView.SurfaceProvider);

        var primaryGroup = new UseCaseGroup.Builder().AddUseCase(_primaryPreview)?.Build()
            ?? throw new InvalidOperationException("Could not build the first preview group.");
        var secondaryGroup = new UseCaseGroup.Builder().AddUseCase(_secondaryPreview)?.Build()
            ?? throw new InvalidOperationException("Could not build the second preview group.");
        var configs = new JavaList<ConcurrentCamera.SingleCameraConfig>
        {
            new(primarySelector, primaryGroup, lifecycleOwner),
            new(secondarySelector, secondaryGroup, lifecycleOwner)
        };

        provider.BindToLifecycle(configs);
    }

    private static CameraSelector BuildPhysicalSelector(string physicalCameraId)
    {
        var builder = new CameraSelector.Builder();
        builder.RequireLensFacing(CameraSelector.LensFacingBack);
        builder.SetPhysicalCameraId(physicalCameraId);
        return builder.Build()
            ?? throw new InvalidOperationException($"Could not select physical camera {physicalCameraId}.");
    }

    private static Preview BuildPreview(int width, int height)
    {
        var strategy = new ResolutionStrategy(
            new AndroidSize(width, height),
            ResolutionStrategy.FallbackRuleClosestHigherThenLower);
        var selectorBuilder = new ResolutionSelector.Builder();
        selectorBuilder.SetResolutionStrategy(strategy);
        var selector = selectorBuilder.Build()
            ?? throw new InvalidOperationException("Could not build a resolution selector.");
        var previewBuilder = new Preview.Builder();
        previewBuilder.SetResolutionSelector(selector);
        return previewBuilder.Build()
            ?? throw new InvalidOperationException("Could not build a CameraX preview.");
    }

    private string BuildSuccessStatus()
    {
        var primaryResolution = _primaryPreview?.ResolutionInfo?.Resolution;
        var secondaryResolution = _secondaryPreview?.ResolutionInfo?.Resolution;
        return "BIND SUCCEEDED\n" +
            $"Left physical ID: {VirtualView.PrimaryCameraId}\n" +
            $"Right physical ID: {VirtualView.SecondaryCameraId}\n" +
            $"Requested each: {VirtualView.RequestedWidth}×{VirtualView.RequestedHeight}\n" +
            $"Actual left: {FormatSize(primaryResolution)}\n" +
            $"Actual right: {FormatSize(secondaryResolution)}\n\n" +
            "Success only counts if both panels contain different, live views.";
    }

    private static string FormatSize(AndroidSize? size) =>
        size is null ? "pending / not reported" : $"{size.Width}×{size.Height}";

    private void Stop()
    {
        Interlocked.Increment(ref _operationVersion);
        _provider?.UnbindAll();
        _primaryPreview = null;
        _secondaryPreview = null;
    }
}
