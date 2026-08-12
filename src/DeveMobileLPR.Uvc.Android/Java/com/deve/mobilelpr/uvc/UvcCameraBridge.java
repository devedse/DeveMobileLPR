package com.deve.mobilelpr.uvc;

import android.content.Context;
import android.hardware.usb.UsbConstants;
import android.hardware.usb.UsbDevice;
import android.hardware.usb.UsbInterface;
import android.os.Handler;
import android.os.HandlerThread;
import android.view.Surface;

import com.serenegiant.usb.Size;
import com.serenegiant.usb.USBMonitor;
import com.serenegiant.usb.UVCCamera;
import com.serenegiant.usb.UVCParam;

import java.nio.ByteBuffer;
import java.util.List;

/** Small, stable API surface over the UVCAndroid/libuvc implementation. */
public final class UvcCameraBridge {
    public interface Listener {
        void onAttached(UsbDevice device);
        void onDetached(UsbDevice device);
        void onPermissionDenied(UsbDevice device);
        void onOpened(UsbDevice device, int width, int height, int framesPerSecond);
        void onFrame(ByteBuffer frame, int width, int height);
        void onError(UsbDevice device, String message);
    }

    private static final int MAX_PREVIEW_WIDTH = 1920;
    private static final int MAX_PREVIEW_HEIGHT = 1080;

    private final USBMonitor monitor;
    private final Listener listener;
    private final HandlerThread callbackThread;
    private UVCCamera camera;
    private UsbDevice selectedDevice;
    private Surface previewSurface;
    private int width;
    private int height;

    public UvcCameraBridge(Context context, Listener listener) {
        if (context == null || listener == null) {
            throw new IllegalArgumentException("context and listener are required");
        }
        this.listener = listener;
        callbackThread = new HandlerThread("DeveMobileLPR-UVC");
        callbackThread.start();
        monitor = new USBMonitor(context.getApplicationContext(), new USBMonitor.OnDeviceConnectListener() {
            @Override public void onAttach(UsbDevice device) { handleAttach(device); }
            @Override public void onDetach(UsbDevice device) { handleDetach(device); }
            @Override public void onDeviceOpen(UsbDevice device, USBMonitor.UsbControlBlock controlBlock, boolean createNew) {
                handleDeviceOpen(device, controlBlock);
            }
            @Override public void onDeviceClose(UsbDevice device, USBMonitor.UsbControlBlock controlBlock) {
                handleDeviceClose(device);
            }
            @Override public void onCancel(UsbDevice device) { listener.onPermissionDenied(device); }
            @Override public void onError(UsbDevice device, USBMonitor.USBException error) {
                listener.onError(device, error.getMessage() != null ? error.getMessage() : error.toString());
            }
        }, new Handler(callbackThread.getLooper()));
        monitor.register();
    }

    public static boolean isUvcDevice(UsbDevice device) {
        if (device == null) return false;
        for (int i = 0; i < device.getInterfaceCount(); i++) {
            UsbInterface usbInterface = device.getInterface(i);
            if (usbInterface.getInterfaceClass() == UsbConstants.USB_CLASS_VIDEO) return true;
        }
        return false;
    }

    public synchronized void selectDevice(UsbDevice device, Surface surface) {
        closeCamera();
        selectedDevice = device;
        previewSurface = surface;
        if (!isUvcDevice(device)) {
            listener.onError(device, "The selected USB device does not expose a UVC video interface.");
            return;
        }
        monitor.requestPermission(device);
    }

    public synchronized void setPreviewSurface(Surface surface) {
        previewSurface = surface;
        if (camera != null && surface != null && surface.isValid()) {
            camera.setPreviewDisplay(surface);
        }
    }

    public synchronized void closeCamera() {
        if (camera != null) {
            try { camera.setFrameCallback(null, 0); } catch (Exception ignored) { }
            try { camera.stopPreview(); } catch (Exception ignored) { }
            try { camera.destroy(true); } catch (Exception ignored) { }
            camera = null;
        }
        width = 0;
        height = 0;
    }

    public synchronized void release() {
        closeCamera();
        selectedDevice = null;
        previewSurface = null;
        monitor.destroy();
        callbackThread.quitSafely();
    }

    private void handleAttach(UsbDevice device) { if (isUvcDevice(device)) listener.onAttached(device); }

    private synchronized void handleDetach(UsbDevice device) {
        if (device != null && device.equals(selectedDevice)) closeCamera();
        if (isUvcDevice(device)) listener.onDetached(device);
    }

    private synchronized void handleDeviceOpen(UsbDevice device, USBMonitor.UsbControlBlock controlBlock) {
        if (selectedDevice == null || !selectedDevice.equals(device)) return;
        try {
            UVCParam param = new UVCParam();
            param.setQuirks(UVCCamera.getRecommendedPlatformQuirks());
            UVCCamera newCamera = new UVCCamera(param);
            int result = newCamera.open(controlBlock);
            if (result != 0) {
                newCamera.destroy(true);
                listener.onError(device, "Unable to open the UVC camera (libuvc error " + result + ").");
                return;
            }

            Size size = choosePreviewSize(newCamera.getSupportedSizeList());
            if (size == null) {
                newCamera.destroy(true);
                listener.onError(device, "The UVC camera did not report a supported video format.");
                return;
            }

            newCamera.setPreviewSize(size);
            if (previewSurface != null && previewSurface.isValid()) newCamera.setPreviewDisplay(previewSurface);
            width = size.width;
            height = size.height;
            newCamera.setFrameCallback(this::handleFrame, UVCCamera.PIXEL_FORMAT_NV21);
            newCamera.startPreview();
            camera = newCamera;
            listener.onOpened(device, size.width, size.height, size.fps);
        } catch (Exception error) {
            closeCamera();
            listener.onError(device, error.getMessage() != null ? error.getMessage() : error.toString());
        }
    }

    private synchronized void handleDeviceClose(UsbDevice device) {
        if (device != null && device.equals(selectedDevice)) closeCamera();
    }

    private void handleFrame(ByteBuffer frame) {
        Listener target;
        int frameWidth;
        int frameHeight;
        synchronized (this) {
            target = listener;
            frameWidth = width;
            frameHeight = height;
        }
        if (frameWidth > 0 && frameHeight > 0) target.onFrame(frame, frameWidth, frameHeight);
    }

    private static Size choosePreviewSize(List<Size> sizes) {
        if (sizes == null || sizes.isEmpty()) return null;
        Size best = null;
        long bestScore = Long.MIN_VALUE;
        for (Size size : sizes) {
            if (size == null || size.width <= 0 || size.height <= 0) continue;
            boolean withinLimit = size.width <= MAX_PREVIEW_WIDTH && size.height <= MAX_PREVIEW_HEIGHT;
            long pixels = (long) size.width * size.height;
            long score = withinLimit ? 100_000_000_000L + pixels : -pixels;
            if (size.type == UVCCamera.FRAME_FORMAT_MJPEG) score += 10_000_000_000L;
            score += Math.min(Math.max(size.fps, 0), 60) * 1_000L;
            if (best == null || score > bestScore) {
                best = size;
                bestScore = score;
            }
        }
        return best;
    }
}
