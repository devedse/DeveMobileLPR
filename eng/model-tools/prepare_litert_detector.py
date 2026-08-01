#!/usr/bin/env python3
"""Create and validate the raw-output YOLO detector used by Android LiteRT."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
import onnx
from ai_edge_litert.interpreter import Interpreter


INPUT_NAME = "images"
BOXES_NAME = "/end2end/Squeeze_output_0"
SCORES_NAME = "/end2end/ReduceMax_output_0"
ONNX_INPUT_SHAPE = [1, 3, 608, 608]
TFLITE_INPUT_SHAPE = [1, 608, 608, 3]
BOXES_SHAPE = [1, 7581, 4]
SCORES_SHAPE = [1, 7581, 1]


def extract_raw(source: Path, destination: Path) -> None:
    model = onnx.load(source)
    inputs = {value.name: value for value in model.graph.input}
    if INPUT_NAME not in inputs:
        raise ValueError(f"Expected ONNX input '{INPUT_NAME}'.")

    outputs = {value.name: value for value in model.graph.value_info}
    outputs.update({value.name: value for value in model.graph.output})
    missing = [name for name in (BOXES_NAME, SCORES_NAME) if name not in outputs]
    if missing:
        raise ValueError(f"Expected pre-NMS ONNX tensors are missing: {', '.join(missing)}")

    destination.parent.mkdir(parents=True, exist_ok=True)
    onnx.utils.extract_model(
        str(source),
        str(destination),
        [INPUT_NAME],
        [BOXES_NAME, SCORES_NAME],
        check_model=True,
        infer_shapes=True,
    )
    validate_onnx(destination)


def validate_onnx(path: Path) -> None:
    model = onnx.load(path)
    onnx.checker.check_model(model)
    inputs = {value.name: tensor_shape(value) for value in model.graph.input}
    outputs = {value.name: tensor_shape(value) for value in model.graph.output}
    require_shape(inputs, INPUT_NAME, ONNX_INPUT_SHAPE, "ONNX input")
    require_shape(outputs, BOXES_NAME, BOXES_SHAPE, "ONNX output")
    require_shape(outputs, SCORES_NAME, SCORES_SHAPE, "ONNX output")


def validate_tflite(path: Path, accuracy_report: Path) -> None:
    interpreter = Interpreter(model_path=str(path))
    inputs = interpreter.get_input_details()
    outputs = interpreter.get_output_details()
    if len(inputs) != 1 or len(outputs) != 2:
        raise ValueError(
            f"Expected one LiteRT input and two outputs, got {len(inputs)} and {len(outputs)}."
        )
    if list(inputs[0]["shape"]) != TFLITE_INPUT_SHAPE or inputs[0]["dtype"] != np.float32:
        raise ValueError(
            f"Expected float32 LiteRT input {TFLITE_INPUT_SHAPE}, got "
            f"{inputs[0]['dtype']} {list(inputs[0]['shape'])}."
        )

    output_shapes = sorted((list(item["shape"]), item["dtype"]) for item in outputs)
    expected_shapes = sorted(
        [(BOXES_SHAPE, np.dtype(np.float32)), (SCORES_SHAPE, np.dtype(np.float32))]
    )
    normalized_shapes = [(shape, np.dtype(dtype)) for shape, dtype in output_shapes]
    if normalized_shapes != expected_shapes:
        raise ValueError(f"Unexpected LiteRT output contracts: {normalized_shapes}.")

    report = json.loads(accuracy_report.read_text(encoding="utf-8"))
    if not report.get("evaluation_pass", False):
        raise ValueError(f"ONNX/LiteRT accuracy validation failed: {accuracy_report}")


def tensor_shape(value: onnx.ValueInfoProto) -> list[int]:
    return [dimension.dim_value for dimension in value.type.tensor_type.shape.dim]


def require_shape(
    tensors: dict[str, list[int]],
    name: str,
    expected: list[int],
    description: str,
) -> None:
    actual = tensors.get(name)
    if actual != expected:
        raise ValueError(f"Expected {description} '{name}' {expected}, got {actual}.")


def main() -> None:
    parser = argparse.ArgumentParser()
    subcommands = parser.add_subparsers(dest="command", required=True)

    extract = subcommands.add_parser("extract")
    extract.add_argument("--source", type=Path, required=True)
    extract.add_argument("--destination", type=Path, required=True)

    validate = subcommands.add_parser("validate")
    validate.add_argument("--model", type=Path, required=True)
    validate.add_argument("--accuracy-report", type=Path, required=True)

    arguments = parser.parse_args()
    if arguments.command == "extract":
        extract_raw(arguments.source, arguments.destination)
    else:
        validate_tflite(arguments.model, arguments.accuracy_report)


if __name__ == "__main__":
    main()
