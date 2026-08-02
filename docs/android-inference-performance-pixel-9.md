# Pixel 9 Android inference performance

This document summarizes the detector inference backends tested on a Google Pixel 9. The measurements were recovered from the on-device diagnostics screenshots and the accompanying Codex investigation from August 2, 2026.

## Summary

| Detector backend | Accelerator | Detector time | Full detector pipeline | Approximate throughput | Outcome |
|---|---|---:|---:|---:|---|
| ONNX Runtime XNNPACK | CPU | About 600 ms | Not recorded separately | About 1.7 FPS at best | Working baseline, but slow |
| LiteRT GPU | GPU through OpenCL/OpenGL | About 100 ms | Not recorded separately | About 10 FPS at best | Fastest tested backend |
| ONNX Runtime WebGPU | GPU through Dawn/Vulkan | About 356-371 ms | About 419-445 ms | About 2.2-2.4 FPS | Stable, but much slower than LiteRT |
| ONNX Runtime WebGPU with graph capture | GPU through Dawn/Vulkan | 893.5 ms for one frame | Invalid measurement | Not applicable | Stalled after the first frame |
| ONNX Runtime NNAPI | Android accelerator API | No measurement | No measurement | Not applicable | Provider failed before model execution |

The FPS values for XNNPACK and LiteRT are simple estimates from the reported per-frame times. They are not measured sustained camera throughput.

## Relative performance

- LiteRT GPU was approximately **6 times faster** than ONNX Runtime XNNPACK CPU.
- Stable ONNX Runtime WebGPU was approximately **1.6-1.7 times faster** than XNNPACK CPU.
- LiteRT GPU was approximately **3.5 times faster** than ONNX Runtime WebGPU for detector inference.
- WebGPU model execution consumed about **85%** of its full detector pipeline time. Preprocessing and postprocessing were not the main bottlenecks.

## Detailed results

### ONNX Runtime XNNPACK

The original Android ONNX path fell back to XNNPACK CPU because NNAPI was unavailable. Detector inference took approximately **600 ms per frame**.

This was the slowest working backend tested on the Pixel 9. Its approximate upper limit was **1.7 detector frames per second**, before accounting for additional camera, OCR, tracking, or UI work.

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

Despite those graph changes, NNAPI was never successfully benchmarked. The available ONNX Runtime Android managed/native combination rejected or lacked the NNAPI provider before the model executed. There is therefore no Pixel 9 NNAPI performance number.

## OCR clarification

The screenshots also showed `ONNX Runtime XNNPACK (2 threads)` for the plate reader. That was the separate OCR session, not a detector fallback from WebGPU.

The clean WebGPU measurement showed `Plate reader x0`, so no OCR ran during that sample. The recorded 419-445 ms pipeline time therefore represents detector work without OCR contamination.

## Not tested on the Pixel 9

The following options were investigated but were not implemented or benchmarked:

- **Qualcomm QNN HTP/NPU:** promising for Snapdragon devices, but Android requires a custom ONNX Runtime build and a calibrated quantized QDQ model.
- **WebNN/WebNPU:** browser-oriented and experimental on Android; it is not a suitable native MAUI inference path.

They must not be included in performance comparisons until they have run successfully on the device with CPU fallback disabled and their accuracy has been validated.

## Conclusion

LiteRT GPU was the clear winner on the Pixel 9 at approximately **100 ms per detector frame**. ONNX Runtime WebGPU was stable after graph capture was removed, but its **356-371 ms model time** made it roughly 3.5 times slower than LiteRT. ONNX Runtime XNNPACK worked as a CPU fallback but was slower again at approximately **600 ms**.

For the current app, LiteRT GPU should remain the production Android detector backend. QNN HTP is the most relevant future experiment if a native ONNX-based NPU path is still desired.
