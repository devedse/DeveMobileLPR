# Pixel 9 Android inference performance

This document summarizes the detector inference backends tested on a Google Pixel 9. The measurements were recovered from the on-device diagnostics screenshots and the accompanying Codex investigation from August 2, 2026.

## Summary

| Detector backend | Accelerator | Detector time | Full detector pipeline | Approximate throughput | Outcome |
|---|---|---:|---:|---:|---|
| ONNX Runtime XNNPACK | CPU | About 403-600 ms | About 467-554 ms in later runs | About 1.7-2.5 FPS | Working fallback, but slow |
| LiteRT GPU | GPU through OpenCL/OpenGL | About 100 ms | Not recorded separately | About 10 FPS at best | Fastest tested backend |
| ONNX Runtime WebGPU | GPU through Dawn/Vulkan | About 356-371 ms | About 419-445 ms | About 2.2-2.4 FPS | Stable, but much slower than LiteRT |
| ONNX Runtime WebGPU with graph capture | GPU through Dawn/Vulkan | 893.5 ms for one frame | Invalid measurement | Not applicable | Stalled after the first frame |
| ONNX Runtime NNAPI | Android accelerator API | No measurement | No measurement | Not applicable | Native provider reached, but the detector was rejected by the Pixel target devices |

The FPS values for XNNPACK and LiteRT are simple estimates from the reported per-frame times. They are not measured sustained camera throughput.

## Relative performance

- LiteRT GPU was approximately **4-6 times faster** than ONNX Runtime XNNPACK CPU.
- Stable ONNX Runtime WebGPU was approximately **1.6-1.7 times faster** than XNNPACK CPU.
- LiteRT GPU was approximately **3.5 times faster** than ONNX Runtime WebGPU for detector inference.
- WebGPU model execution consumed about **85%** of its full detector pipeline time. Preprocessing and postprocessing were not the main bottlenecks.

## Detailed results

### ONNX Runtime XNNPACK

The original Android ONNX path measured approximately **600 ms per detector frame**. Later diagnostic builds measured XNNPACK startup medians of **403.0 ms** and **447.4 ms**. Live detector model time in the final FP32 NNAPI experiment was **409.6 ms**.

The detector-only range corresponds to approximately **1.7-2.5 detector frames per second**, before accounting for additional camera, OCR, tracking, or UI work.

### LiteRT GPU

The raw detector was converted from ONNX to a float32 LiteRT model. LiteRT used its Android GPU accelerator through OpenCL/OpenGL and completed detector inference in approximately **100 ms per frame**.

This was the fastest working backend tested, with an approximate detector-only upper limit of **10 frames per second**. The diagnostics available at the time did not provide a separate breakdown for preprocessing and output processing.

### ONNX Runtime WebGPU

The stable WebGPU build used the raw ONNX detector with graph capture disabled. Two screenshot sets produced consistent results:

| Measurement | First screenshot set | Later screenshot set |
|---|---:|---:|
| Startup benchmark median | 368.1 ms | 369.8 ms |
| Live detector model | 356.4 ms | About 367-371 ms |
| Preprocessing | 62.6 ms | About 62-66 ms |
| Output processing | Not separately reported | 0.1 ms |
| Full detector pipeline | 419-445 ms | 432-437 ms |

The close agreement between startup and live detector timings confirmed that WebGPU was repeatedly executing the model without detector fallback. The resulting throughput was approximately **2.2-2.4 detector frames per second**.

The backend was reported as `ONNX Runtime WebGPU Vulkan NCHW`. ONNX Runtime uses Dawn over Vulkan for this path on Android.

### WebGPU graph-capture experiment

An earlier WebGPU build enabled graph capture. It completed one live frame and displayed **893.5 ms**, but the value and replacement counter remained unchanged in screenshots taken several seconds apart.

This was not a valid steady-state benchmark. Graph replay was blocked by incompatible live tensor lifetimes, so graph capture was disabled. The 893.5 ms result must not be compared with the working backends.

### NNAPI experiment

The raw ONNX detector was simplified for NNAPI compatibility:

- The ONNX non-maximum suppression tail was removed and replaced by shared C# postprocessing.
- A redundant singleton `ReduceMax` was bypassed.
- An uneven `Split` was replaced by two static `Slice` operations.

The original managed API could not reach the Android-only provider registration from the platform-neutral inference assembly. A native bridge subsequently registered NNAPI successfully with both NNAPI CPU execution and ONNX Runtime CPU fallback disabled.

The detector then reached two distinct native failures:

- **FP16:** Android model compilation ended with `ANEURALNETWORKS_OP_FAILED`.
- **FP32:** ONNX Runtime returned `ShapeInferenceNotRegistered` for the reported `[google-edgetpu], Type [4]` target-device set.

The FP32 result rules out reduced precision as the root cause. The bridge worked, but this detector graph was not accepted by the Pixel 9 NNAPI target devices. NNAPI was therefore never benchmarked.

## OCR clarification

The screenshots also showed `ONNX Runtime XNNPACK (2 threads)` for the plate reader. That was the separate OCR session, not a detector fallback from WebGPU. OCR NNAPI was rejected because some nodes remained assigned to ONNX Runtime CPU while CPU fallback was explicitly disabled.

The clean WebGPU measurement showed `Plate reader x0`, so no OCR ran during that sample. The recorded 419-445 ms pipeline time therefore represents detector work without OCR contamination.

A later XNNPACK frame performed four OCR reads in **248.4 ms**, approximately **62 ms per read**. This is an aggregate from one frame, not a sustained OCR benchmark. Android LiteRT OCR was implemented after this measurement and must be benchmarked on the Pixel 9 before claiming a speedup.

## Not tested on the Pixel 9

The following options were investigated but were not implemented or benchmarked:

- **Qualcomm QNN HTP/NPU:** promising for Snapdragon devices, but Android requires a custom ONNX Runtime build and a calibrated quantized QDQ model.
- **WebNN/WebNPU:** browser-oriented and experimental on Android; it is not a suitable native MAUI inference path.

They must not be included in performance comparisons until they have run successfully on the device with CPU fallback disabled and their accuracy has been validated.

## Conclusion

LiteRT GPU was the clear detector winner on the Pixel 9 at approximately **100 ms per frame**. ONNX Runtime WebGPU was stable after graph capture was removed, but its **356-371 ms model time** made it roughly 3.5 times slower. ONNX Runtime XNNPACK remained slower at approximately **403-600 ms**.

The production Android APK therefore uses LiteRT for both detector and OCR, with GPU selected only after a successful warm inference and explicit LiteRT CPU fallback. The detector decision is supported by device measurements; LiteRT OCR still requires a new Pixel 9 measurement and accuracy check against real plate crops.
