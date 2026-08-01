# Third-party notices

DeveMobileLPR loads the following freely redistributable model assets at build time. The model binaries are deliberately not committed to source control; `eng/Download-Models.ps1` pins downloaded inputs and `eng/Generate-LiteRt-Models.ps1` pins generated Android outputs.

| Component | Upstream | License | Pinned SHA-256 |
|---|---|---|---|
| YOLOv9-S 608 license-plate detector | [ankandrew/open-image-models](https://github.com/ankandrew/open-image-models) | MIT | `2B878B38D9AA07B6DDC3EA75C4FFCB39869BC5C218E0A14002F60AB2F7B0BE9A` |
| CCT-S V2 global plate OCR | [ankandrew/fast-plate-ocr](https://github.com/ankandrew/fast-plate-ocr) | MIT | `384BBBD2CEA3EF54761D3DF70822EF3A349EE1A112AEAFDDBE0E3BA06BC6E47B` |

The Android detector is mechanically derived from the pinned YOLO model above. Its raw ONNX SHA-256 is `8886A067DD514404E99FDF1CFC642827303A4700E3D9FFE829DADC446BB94BCE`; its packaged float32 LiteRT SHA-256 is `EE20A2F2DAAD51525A449E2A7E388965E4F9DEC5F39CB8D0348C21232FFAA1E2`.

| Build/runtime component | Upstream | License |
|---|---|---|
| onnx2tf model converter | [PINTO0309/onnx2tf](https://github.com/PINTO0309/onnx2tf) | MIT |
| Google AI Edge LiteRT Android runtime and .NET binding | [Google AI Edge LiteRT](https://github.com/google-ai-edge/LiteRT) / [Microsoft .NET for Android](https://github.com/dotnet/android-libraries) | Apache-2.0 / MIT |

The RDW database builder uses these additional inputs:

| Component | Upstream | License |
|---|---|---|
| RDW Gekentekende voertuigen and brandstof datasets | [RDW Open Data](https://opendata.rdw.nl/) | CC0 1.0 |
| Sylvan.Data.Csv 1.4.4 | [MarkPflug/Sylvan](https://github.com/MarkPflug/Sylvan) | MIT |

NuGet dependencies retain their respective upstream licenses. Release dependency manifests should be reviewed before public distribution.
