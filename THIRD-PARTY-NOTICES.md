# Third-party notices

DeveMobileLPR loads the following freely redistributable model assets at build time. The model binaries are deliberately not committed to source control; `eng/Download-Models.ps1` pins and verifies them.

| Component | Upstream | License | Pinned SHA-256 |
|---|---|---|---|
| YOLOv9-S 608 license-plate detector | [ankandrew/open-image-models](https://github.com/ankandrew/open-image-models) | MIT | `2B878B38D9AA07B6DDC3EA75C4FFCB39869BC5C218E0A14002F60AB2F7B0BE9A` |
| CCT-S V2 global plate OCR | [ankandrew/fast-plate-ocr](https://github.com/ankandrew/fast-plate-ocr) | MIT | `384BBBD2CEA3EF54761D3DF70822EF3A349EE1A112AEAFDDBE0E3BA06BC6E47B` |

NuGet dependencies retain their respective upstream licenses. Release dependency manifests should be reviewed before public distribution.
