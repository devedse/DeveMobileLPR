using Android.Content.PM;
using Android.Views;
using DeveMobileLPR.AndroidApp.ViewModels;

namespace DeveMobileLPR.AndroidApp.Views;

public partial class DrivePage : ContentPage
{
    private readonly DriveViewModel _viewModel;

    internal DrivePage(DriveViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _viewModel.DriveModeChanged += DriveModeChanged;
        SizeChanged += PageSizeChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        ApplyDriveMode(_viewModel.IsDriving);
        await _viewModel.InitializeAsync();
    }

    private void DriveModeChanged(object? sender, bool isDriving) => ApplyDriveMode(isDriving);

    private void PageSizeChanged(object? sender, EventArgs args)
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        var compactLandscape = Width > Height;
        ReadyPortraitContent.IsVisible = !compactLandscape;
        ReadyLandscapeContent.IsVisible = compactLandscape;
        ReadyPanel.MaximumWidthRequest = compactLandscape ? 900 : 570;
        ReadyPanel.Padding = compactLandscape ? new Thickness(24, 16) : new Thickness(30);
        ReadyPanel.VerticalOptions = LayoutOptions.Center;
        ReadyPanel.Margin = compactLandscape ? new Thickness(26, 4) : Thickness.Zero;
    }

    private void ApplyDriveMode(bool isDriving)
    {
        Shell.SetTabBarIsVisible(this, !isDriving);
        if (Platform.CurrentActivity is not { } activity || activity.Window?.DecorView is not { } decor)
        {
            return;
        }

#pragma warning disable CS0618
        activity.RequestedOrientation = isDriving ? ScreenOrientation.SensorLandscape : ScreenOrientation.Unspecified;
        var flags = isDriving
            ? SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation | SystemUiFlags.LayoutStable
            : SystemUiFlags.Visible;
        decor.SystemUiVisibility = (StatusBarVisibility)flags;
#pragma warning restore CS0618
    }
}
