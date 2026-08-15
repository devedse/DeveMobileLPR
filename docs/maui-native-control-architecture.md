# .NET MAUI native-control architecture

This document describes how this repository should combine shared XAML, cross-platform controls,
platform handlers, native views, application services, and long-running device resources. The
camera preview is the worked example, but the rules also apply to maps, media players, scanners,
and other native surfaces.

The design follows the current [.NET MAUI custom-handler guidance](https://learn.microsoft.com/dotnet/maui/user-interface/handlers/create?view=net-maui-10.0), the [.NET MAUI ContentView guidance](https://learn.microsoft.com/dotnet/maui/user-interface/controls/contentview?view=net-maui-10.0), and the handler-based [CommunityToolkit MAUI CameraView](https://github.com/CommunityToolkit/Maui/tree/main/src/CommunityToolkit.Maui.Camera).

## Start by identifying the kind of control

There are two different reasons to create a custom control. They should not be implemented the
same way.

### Shared visual composition: use XAML and `ContentView`

Use a XAML-backed `ContentView` when the control is a reusable composition of MAUI elements such
as grids, labels, buttons, overlays, and other controls.

```text
DrivePreviewPresenter.xaml
DrivePreviewPresenter.xaml.cs
```

XAML owns the shared visual tree. The code-behind defines bindable properties and bridges UI-only
events. It must not acquire cameras, run recognition, or own application workflows.

### Native platform surface: use `View` and a handler

Use a handler-backed `View` when Android, Windows, or another platform must supply the actual
rendering object. A camera preview is such a surface: Android needs `PreviewView`/`TextureView`,
while Windows needs WinUI media controls.

```text
CameraPreview.cs                         shared virtual view
Handlers/CameraPreviewHandler.cs         shared mapper and lifecycle shell
Platforms/Android/Camera/...             Android native host and handler partial
Platforms/Windows/Camera/...             Windows native host and handler partial
```

The virtual view does not require a matching XAML file. It is a platform-neutral API and a layout
slot. A XAML page or `ContentView` consumes it just like a built-in MAUI control.

## Responsibilities by layer

### Page and presenter

- Compose shared UI in XAML.
- Bind presentation state and commands from the view model.
- Put the native surface and its MAUI overlay in the same layout rectangle.
- Avoid native APIs and camera lifecycle decisions.

### Cross-platform virtual view

- Derive from `View` for a native surface.
- Expose the smallest cross-platform input API as bindable properties or commands.
- Expose platform-reported state as read-only properties or immutable snapshots.
- Never expose Android, WinUI, or other native types.

Inputs and outputs must be visibly different. For example, `IsMultiSource` is an input, while
`SourceViewports` is an output reported by the native layout.

### Shared handler

- Define property and command mappers.
- Translate virtual-view changes into small platform operations.
- Keep the `CreatePlatformView`/`ConnectHandler`/`DisconnectHandler` contract consistent.
- Contain no recognition, persistence, navigation, or product workflow.

### Platform handler partial

- Create the native host.
- Connect the host to a platform adapter when the handler connects.
- Release that input lease when the handler disconnects.
- Do not construct a large camera graph inline; delegate that to a platform factory.

### Native preview host

- Own only the platform visual hierarchy.
- Expose the native surfaces needed by the platform input adapter.
- Expose the visual surfaces from which the platform adapter can report layout geometry.
- Not know about recognition, trips, storage, or view models.

### Platform input factory and adapter

- Compose CameraX/Camera2/MediaCapture/LL-HLS implementations.
- Translate native frames into the shared `Yuv420Frame` contract.
- Use dependencies supplied through DI rather than having the handler resolve every dependency.
- Keep source selection and capture details platform-specific.

### Application coordinator

- Own drive state, recognition submission, consensus, and persistence coordination.
- Consume the platform-neutral `IDriveVideoInput` contract.
- Not know about MAUI handlers or native controls.

## Data flow

```text
ViewModel inputs
    -> XAML presenter
    -> virtual view bindable properties/commands
    -> handler mapper
    -> native preview host / platform input

Native layout and status
    -> immutable/read-only virtual-view output
    -> XAML presenter
    -> overlay

Native YUV frames
    -> IDriveVideoInput
    -> DriveCoordinator
    -> shared recognition pipeline
    -> overlay view model state
```

High-frequency frames must not travel through MAUI bindable properties. Only low-frequency UI
state such as geometry, readiness, or an error snapshot belongs there.

## Lifecycle rules

1. `CreatePlatformView` creates the native visual host and has no external workflow side effects.
2. `ConnectHandler` attaches the platform input and retains its lease.
3. `DisconnectHandler` releases that lease.
4. Camera disposal is asynchronous, serialized, owned, and has surfaced failures. Do not use an
   unowned `_ = DisposeAsync()` call.
5. A replacement camera input must wait for the previous native session to release its device
   resources before initializing.
6. Handler recreation is a UI lifecycle event, not user intent to start or stop a trip.
7. The application coordinator remains the authority for whether recognition is running.

Because MAUI handler callbacks are synchronous, this repository uses an owned lifetime service to
serialize asynchronous native-input disposal. The service can attach the replacement immediately
as a platform-neutral input, while its initialization waits for the preceding teardown task.

## Property and command mapping

Use property mappers for durable state:

```csharp
[nameof(CameraPreview.IsMultiSource)] = MapPresentationMode
```

Use command mappers for explicit instructions carrying optional data. Do not use bindable
properties as a high-rate message bus.

Platform outputs should use a read-only bindable property key:

```csharp
private static readonly BindablePropertyKey SourceViewportsPropertyKey =
    BindableProperty.CreateReadOnly(...);
```

Only the control/handler bridge can update it; ordinary XAML consumers can bind to it but cannot
accidentally replace it.

## Camera preview worked example

The shared composition is:

```text
DrivePage
    -> DrivePreviewPresenter.xaml
        -> CameraPreview                    native surface slot
        -> DriveOverlayView                 shared MAUI drawing surface
```

At runtime:

```text
CameraPreview
    -> CameraPreviewHandler
        -> AndroidCameraPreviewHost or WindowsCameraPreviewHost
        -> AndroidDriveVideoInputFactory or WindowsDriveVideoInputFactory
        -> DriveVideoInputLifetime
        -> DriveCoordinator
        -> recognition
```

The Android input adapter derives actual normalized source-panel bounds from its native host.
`CameraPreview` publishes them as read-only `SourceViewports`. `DrivePreviewPresenter` binds those bounds directly to
`DriveOverlayView`, ensuring the preview and detection projection use the same panels.

## Review checklist

- Does a XAML file describe shared MAUI composition rather than duplicate a native visual tree?
- Is the raw native surface a small `View` API with a registered handler?
- Does the mapper contain every virtual property that changes native presentation?
- Does `CreatePlatformView` avoid camera/coordinator side effects?
- Are native host, input factory, capture adapter, and application coordinator separate owners?
- Are output properties read-only and platform-neutral?
- Are frame paths bounded and independent of MAUI binding?
- Is teardown awaited by an owned serialized lifetime?
- Can a handler be recreated without leaking a camera or racing the next session?
- Are preview geometry and overlay geometry sourced from one snapshot?
