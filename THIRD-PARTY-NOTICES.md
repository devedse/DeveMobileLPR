# Third-party notices

DeveMobileLPR loads the following freely redistributable model assets at build time. The model binaries are deliberately not committed to source control; `eng/Download-Models.ps1` pins downloaded inputs and `eng/Generate-LiteRt-Models.ps1` pins generated Android outputs.

| Component | Upstream | License | Pinned SHA-256 |
|---|---|---|---|
| YOLOv9-S 608 license-plate detector | [ankandrew/open-image-models](https://github.com/ankandrew/open-image-models) | MIT | `2B878B38D9AA07B6DDC3EA75C4FFCB39869BC5C218E0A14002F60AB2F7B0BE9A` |
| CCT-S V2 global plate OCR | [ankandrew/fast-plate-ocr](https://github.com/ankandrew/fast-plate-ocr) | MIT | `384BBBD2CEA3EF54761D3DF70822EF3A349EE1A112AEAFDDBE0E3BA06BC6E47B` |

The Android models are mechanically derived from the pinned ONNX models above. The detector's raw ONNX SHA-256 is `291F31E43FF4DA82C29168960AC12B672F9D57CA04A83594B87F8BAEA108B49F` and its packaged float32 LiteRT SHA-256 is `2D3CF7D206197A0BC719C25422254EFC255B81F9495825D6DBA5A7D770A39433`. The packaged float32 LiteRT OCR SHA-256 is `215049B9D372B7DBB2BA392E85E0E1079681085F66FE92A9884B00CC6681F25C`.

| Build/runtime component | Upstream | License |
|---|---|---|
| onnx2tf model converter | [PINTO0309/onnx2tf](https://github.com/PINTO0309/onnx2tf) | MIT |
| Google AI Edge LiteRT Android runtime and .NET binding | [Google AI Edge LiteRT](https://github.com/google-ai-edge/LiteRT) / [Microsoft .NET for Android](https://github.com/dotnet/android-libraries) | Apache-2.0 / MIT |
| SixLabors.ImageSharp 3.1.12 | [Six Labors ImageSharp](https://github.com/SixLabors/ImageSharp/tree/v3.1.12) | Six Labors Split License 1.0 |
| Leaflet 1.9.4 | [Leaflet](https://github.com/Leaflet/Leaflet/tree/v1.9.4) | BSD-2-Clause |
| Leaflet.markercluster 1.5.3 | [Leaflet.markercluster](https://github.com/Leaflet/Leaflet.markercluster/tree/v1.5.3) | MIT |
| Known-vehicle Bell, Confirm, Glass, and Pulse sounds | [Kenney Interface Sounds](https://kenney.nl/assets/interface-sounds) | CC0 1.0 |
| Known-vehicle Scanner sound | [Kenney Sci-fi Sounds](https://kenney.nl/assets/sci-fi-sounds) | CC0 1.0 |
| Known-vehicle Funny horn sound | [Bicycle Horn](https://opengameart.org/content/bicycle-horn), AntumDeluge | CC0 1.0 |
| Known-vehicle Short car signal sound | [Car signal](https://opengameart.org/content/car-signal), Yaroslav_Novikov | CC0 1.0 |
| Known-vehicle Engine start sound | [Car engine Start Up 02](https://opengameart.org/content/car-engine-start-up-02), looneybits | CC0 1.0 |
| Known-vehicle Car door sound | [Car_door_SFX](https://opengameart.org/content/cardoorsfx), looneybits | CC0 1.0 |
| Known-vehicle Kalimba sound | [Kalimba Two-Notes](https://opengameart.org/content/kalimba-two-notes), railkill | CC0 1.0 |
| Known-vehicle Steam whistle sound | [Steam whistle](https://opengameart.org/content/steam-whistle), bart | CC0 1.0 |
| Known-vehicle Applause sound | [Well Done](https://opengameart.org/content/well-done), qubodup | CC0 1.0 |
| Known-vehicle Orchestral chimes sound | [UI Sound Effects](https://opengameart.org/content/ui-sound-effects-button-clicks-user-feedback-notifications), Robin Lamb | CC0 1.0 |
| Known-vehicle Bell ding sound | [Bell dings/chimes](https://opengameart.org/content/bell-dingschimes), PWL | CC0 1.0 |

The RDW database builder uses these additional inputs:

| Component | Upstream | License |
|---|---|---|
| RDW Gekentekende voertuigen and brandstof datasets | [RDW Open Data](https://opendata.rdw.nl/) | CC0 1.0 |
| Sylvan.Data.Csv 1.4.4 | [MarkPflug/Sylvan](https://github.com/MarkPflug/Sylvan) | MIT |

NuGet dependencies retain their respective upstream licenses. Release dependency manifests should be reviewed before public distribution.
