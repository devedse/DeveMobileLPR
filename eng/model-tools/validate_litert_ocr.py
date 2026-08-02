#!/usr/bin/env python3
"""Validate the generated CCT-S V2 LiteRT OCR model contract and accuracy."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from ai_edge_litert.interpreter import Interpreter


INPUT_SHAPE = [1, 64, 128, 3]
PLATE_SHAPE = [1, 10, 37]
REGION_SHAPE = [1, 66]


def validate(model_path: Path, accuracy_report_path: Path) -> None:
    interpreter = Interpreter(model_path=str(model_path))
    inputs = interpreter.get_input_details()
    outputs = interpreter.get_output_details()
    if len(inputs) != 1 or len(outputs) != 2:
        raise ValueError(
            f"Expected one LiteRT input and two outputs, got {len(inputs)} and {len(outputs)}."
        )

    input_tensor = inputs[0]
    if list(input_tensor["shape"]) != INPUT_SHAPE or input_tensor["dtype"] != np.uint8:
        raise ValueError(
            f"Expected uint8 LiteRT input {INPUT_SHAPE}, got "
            f"{input_tensor['dtype']} {list(input_tensor['shape'])}."
        )

    output_shapes = sorted(
        (list(output["shape"]), np.dtype(output["dtype"])) for output in outputs
    )
    expected_shapes = sorted(
        [(PLATE_SHAPE, np.dtype(np.float32)), (REGION_SHAPE, np.dtype(np.float32))]
    )
    if output_shapes != expected_shapes:
        raise ValueError(f"Unexpected LiteRT OCR output contracts: {output_shapes}.")

    report = json.loads(accuracy_report_path.read_text(encoding="utf-8"))
    if not report.get("evaluation_pass", False):
        raise ValueError(
            f"ONNX/LiteRT OCR accuracy validation failed: {accuracy_report_path}"
        )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--accuracy-report", type=Path, required=True)
    arguments = parser.parse_args()
    validate(arguments.model, arguments.accuracy_report)


if __name__ == "__main__":
    main()