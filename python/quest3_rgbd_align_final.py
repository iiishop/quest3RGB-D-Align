from __future__ import annotations

import argparse
import json
import math
import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np


class Any2FullConfigurationError(RuntimeError):
    pass


ANY2FULL_SETUP_MESSAGE = """\
Any2Full depth completion was requested, but Any2Full is not configured.

This repository does not vendor Any2Full or its model weights. Configure it explicitly:

1. Clone/install Any2Full separately.
   Example:
     git clone <ANY2FULL_REPO_URL> /path/to/Any2Full

2. Create an Any2Full-compatible Python environment and install its dependencies.
   Use the instructions from the Any2Full project. Keep it separate from this RGB-D
   alignment environment if Any2Full requires a different torch/CUDA version.

3. Download the required checkpoint, for example:
     /path/to/Any2Full/checkpoints/Any2Full_vitl.pth.tar

4. Run this script with explicit paths:
     python quest3_rgbd_align_final.py <capture_dir> --complete-depth ^
       --any2full-dir /path/to/Any2Full ^
       --any2full-venv-python /path/to/Any2Full/.venv/Scripts/python.exe ^
       --any2full-checkpoint /path/to/Any2Full/checkpoints/Any2Full_vitl.pth.tar

Expected Any2Full entry point:
  <any2full-dir>/any2full_infer.py

Expected interface:
  any2full_infer.py --rgb <rgb.jpg> --depth <aligned_depth_m.npy> --out <dense_depth_any2full.npy> --checkpoint <ckpt> --encoder <vits|vitb|vitl>
"""


def load_meta(capture_dir: Path) -> dict:
    with (capture_dir / "meta.json").open("r", encoding="utf-8") as f:
        return json.load(f)


def load_rgb(capture_dir: Path) -> np.ndarray:
    bgr = cv2.imread(str(capture_dir / "rgb.jpg"), cv2.IMREAD_COLOR)
    if bgr is None:
        raise FileNotFoundError(capture_dir / "rgb.jpg")
    return cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)


def load_depth_raw(capture_dir: Path, width: int, height: int) -> np.ndarray:
    raw = np.fromfile(capture_dir / "depth.raw", dtype=np.float32)
    expected = width * height
    if raw.size != expected:
        raise ValueError(f"{capture_dir / 'depth.raw'} has {raw.size} floats, expected {expected}")
    return raw.reshape(height, width)


def quaternion_to_matrix(q_xyzw: np.ndarray) -> np.ndarray:
    q = q_xyzw.astype(np.float64)
    norm = np.linalg.norm(q)
    if norm == 0:
        return np.eye(3, dtype=np.float32)
    x, y, z, w = q / norm
    return np.array(
        [
            [1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y - z * w), 2.0 * (x * z + y * w)],
            [2.0 * (x * y + z * w), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z - x * w)],
            [2.0 * (x * z - y * w), 2.0 * (y * z + x * w), 1.0 - 2.0 * (x * x + y * y)],
        ],
        dtype=np.float32,
    )


def projection_from_depth_fov(depth_meta: dict) -> np.ndarray:
    left = float(depth_meta["fov_left"])
    right = float(depth_meta["fov_right"])
    top = float(depth_meta["fov_top"])
    bottom = float(depth_meta["fov_bottom"])
    near = float(depth_meta["near_z"])
    far = float(depth_meta.get("far_z", math.inf))

    x = 2.0 / (right + left)
    y = 2.0 / (top + bottom)
    a = (right - left) / (right + left)
    b = (top - bottom) / (top + bottom)
    if math.isinf(far) or far < near:
        c = -1.0
        d = -2.0 * near
    else:
        c = -(far + near) / (far - near)
        d = -(2.0 * far * near) / (far - near)

    return np.array(
        [[x, 0.0, a, 0.0], [0.0, y, b, 0.0], [0.0, 0.0, c, d], [0.0, 0.0, -1.0, 0.0]],
        dtype=np.float32,
    )


def unity_trs(position: np.ndarray, rotation: np.ndarray, scale: tuple[float, float, float]) -> np.ndarray:
    mat = np.eye(4, dtype=np.float32)
    mat[:3, :3] = rotation @ np.diag(np.array(scale, dtype=np.float32))
    mat[:3, 3] = position
    return mat


def matrix_from_meta(depth_meta: dict) -> np.ndarray:
    for field in ("descriptor_reprojection_matrix", "reprojection_matrix"):
        values = depth_meta.get(field)
        if isinstance(values, list) and len(values) == 16:
            return np.array(values, dtype=np.float32).reshape(4, 4)

    pos = np.array(
        [
            float(depth_meta["pose_position_x"]),
            float(depth_meta["pose_position_y"]),
            float(depth_meta["pose_position_z"]),
        ],
        dtype=np.float32,
    )
    quat = np.array(
        [
            float(depth_meta["pose_rotation_x"]),
            float(depth_meta["pose_rotation_y"]),
            float(depth_meta["pose_rotation_z"]),
            float(depth_meta["pose_rotation_w"]),
        ],
        dtype=np.float32,
    )
    projection = projection_from_depth_fov(depth_meta)
    view = np.linalg.inv(unity_trs(pos, quaternion_to_matrix(quat), (1.0, 1.0, -1.0)))
    return projection @ view


def raw_depth_to_linear_m(raw_depth: np.ndarray, depth_meta: dict) -> np.ndarray:
    zbuffer_x = float(depth_meta["zbuffer_x"])
    zbuffer_y = float(depth_meta["zbuffer_y"])
    ndc = raw_depth.astype(np.float32) * 2.0 - 1.0
    return np.divide(
        np.float32(zbuffer_x),
        ndc + np.float32(zbuffer_y),
        out=np.zeros_like(raw_depth, dtype=np.float32),
        where=np.abs(ndc + np.float32(zbuffer_y)) > 1e-8,
    )


def unproject_depth_to_world(depth_m: np.ndarray, depth_meta: dict, valid: np.ndarray) -> np.ndarray:
    ys, xs = np.where(valid)
    if xs.size == 0:
        return np.empty((0, 3), dtype=np.float32)

    h, w = depth_m.shape
    z = depth_m[ys, xs].astype(np.float32)
    zbuffer_x = float(depth_meta["zbuffer_x"])
    zbuffer_y = float(depth_meta["zbuffer_y"])
    ndc_x = (xs.astype(np.float32) + 0.5) / max(w, 1) * 2.0 - 1.0
    ndc_y = (ys.astype(np.float32) + 0.5) / max(h, 1) * 2.0 - 1.0
    ndc_z = zbuffer_x / np.maximum(z, 1e-6) - zbuffer_y

    clip = np.stack([ndc_x, ndc_y, ndc_z, np.ones_like(ndc_z)], axis=0)
    depth_to_world = np.linalg.inv(matrix_from_meta(depth_meta))
    world_h = depth_to_world @ clip
    world_w = np.where(np.abs(world_h[3]) < 1e-8, 1e-8, world_h[3])
    return (world_h[:3] / world_w).T.astype(np.float32)


def world_to_rgb_camera(points_world: np.ndarray, rgb_meta: dict) -> np.ndarray:
    pos = np.array(
        [
            float(rgb_meta["pose_position_x"]),
            float(rgb_meta["pose_position_y"]),
            float(rgb_meta["pose_position_z"]),
        ],
        dtype=np.float32,
    )
    quat = np.array(
        [
            float(rgb_meta["pose_rotation_x"]),
            float(rgb_meta["pose_rotation_y"]),
            float(rgb_meta["pose_rotation_z"]),
            float(rgb_meta["pose_rotation_w"]),
        ],
        dtype=np.float32,
    )
    rot = quaternion_to_matrix(quat)
    return (points_world - pos[None, :]) @ rot


def project_rgb(points_rgb: np.ndarray, rgb_meta: dict) -> tuple[np.ndarray, np.ndarray]:
    fx = float(rgb_meta["focal_length_x"])
    fy = float(rgb_meta["focal_length_y"])
    cx = float(rgb_meta["principal_point_x"])
    cy = float(rgb_meta["principal_point_y"])
    height = int(rgb_meta["resolution_h"])
    z = np.maximum(points_rgb[:, 2], 1e-6)
    u = points_rgb[:, 0] * fx / z + cx
    sensor_y = points_rgb[:, 1] * fy / z + cy
    v = (height - 1) - sensor_y
    return np.rint(u).astype(np.int32), np.rint(v).astype(np.int32)


def depth_colors(depth_values: np.ndarray, min_depth: float, max_depth: float) -> np.ndarray:
    norm = np.clip((depth_values - min_depth) / max(max_depth - min_depth, 1e-6), 0.0, 1.0)
    colors = np.zeros((depth_values.shape[0], 3), dtype=np.uint8)
    colors[:, 0] = (255.0 * (1.0 - norm)).astype(np.uint8)
    colors[:, 1] = (70.0 * (1.0 - np.abs(norm - 0.5) * 2.0)).astype(np.uint8)
    colors[:, 2] = (255.0 * norm).astype(np.uint8)
    return colors


def make_overlay(rgb: np.ndarray, aligned_depth: np.ndarray, min_depth: float, max_depth: float) -> np.ndarray:
    yy, xx = np.where(aligned_depth > 0)
    if xx.size == 0:
        return rgb.copy()
    depths = aligned_depth[yy, xx]
    lo = max(min_depth, float(np.percentile(depths, 1)))
    hi = min(max_depth, float(np.percentile(depths, 99)))
    colors = depth_colors(depths, lo, hi)
    overlay = rgb.copy()
    for dy in range(-2, 3):
        for dx in range(-2, 3):
            if dx * dx + dy * dy > 4:
                continue
            y2 = yy + dy
            x2 = xx + dx
            inside = (x2 >= 0) & (x2 < aligned_depth.shape[1]) & (y2 >= 0) & (y2 < aligned_depth.shape[0])
            overlay[y2[inside], x2[inside]] = colors[inside]
    return cv2.addWeighted(overlay, 0.38, rgb, 0.62, 0.0)


def run_any2full_completion(
    rgb_path: Path,
    sparse_depth_path: Path,
    output_path: Path,
    any2full_dir: Path,
    any2full_venv_python: str,
    checkpoint_path: str | None = None,
    encoder: str = "vitl",
) -> None:
    """Run Any2Full dense depth completion on an RGB + sparse depth pair.

    Calls any2full_infer.py as a subprocess in its own Python environment.
    """
    if any2full_venv_python is None:
        raise Any2FullConfigurationError(ANY2FULL_SETUP_MESSAGE)
    if not any2full_dir.exists():
        raise Any2FullConfigurationError(
            f"Any2Full directory does not exist: {any2full_dir}\n\n{ANY2FULL_SETUP_MESSAGE}"
        )
    python_path = Path(any2full_venv_python)
    if not python_path.exists():
        raise Any2FullConfigurationError(
            f"Any2Full Python executable does not exist: {python_path}\n\n{ANY2FULL_SETUP_MESSAGE}"
        )
    infer_script = any2full_dir / "any2full_infer.py"
    if not infer_script.exists():
        raise Any2FullConfigurationError(
            f"Any2Full inference script not found: {infer_script}\n\n{ANY2FULL_SETUP_MESSAGE}"
        )

    ckpt = checkpoint_path or str(any2full_dir / "checkpoints" / "Any2Full_vitl.pth.tar")
    if not Path(ckpt).exists():
        raise Any2FullConfigurationError(
            f"Any2Full checkpoint does not exist: {ckpt}\n\n{ANY2FULL_SETUP_MESSAGE}"
        )

    cmd = [
        str(python_path),
        str(infer_script),
        "--rgb", str(rgb_path),
        "--depth", str(sparse_depth_path),
        "--out", str(output_path),
        "--checkpoint", ckpt,
        "--encoder", encoder,
    ]
    result = subprocess.run(cmd, capture_output=True, text=True, cwd=str(any2full_dir), timeout=300)
    if result.returncode != 0:
        raise RuntimeError(f"Any2Full failed (exit {result.returncode}):\nSTDERR: {result.stderr}\nSTDOUT: {result.stdout}")
    print(f"  Any2Full: {result.stdout.strip()}")


def write_ascii_ply(path: Path, points: np.ndarray, colors: np.ndarray) -> None:
    with path.open("w", encoding="ascii", newline="\n") as f:
        f.write("ply\nformat ascii 1.0\n")
        f.write("comment coordinates are RGB camera space: x right, y up, z forward, units metres\n")
        f.write(f"element vertex {points.shape[0]}\n")
        f.write("property float x\nproperty float y\nproperty float z\n")
        f.write("property uchar red\nproperty uchar green\nproperty uchar blue\nend_header\n")
        for p, c in zip(points, colors, strict=True):
            f.write(f"{p[0]:.6f} {p[1]:.6f} {p[2]:.6f} {int(c[0])} {int(c[1])} {int(c[2])}\n")


def align_rgbd_capture(
    capture_dir: str | Path,
    output_dir: str | Path | None = None,
    min_depth: float = 0.2,
    max_depth: float = 8.0,
    complete_depth: bool = False,
    any2full_dir: str | Path | None = None,
    any2full_venv_python: str | None = None,
    any2full_checkpoint: str | None = None,
    any2full_encoder: str = "vitl",
) -> dict:
    capture_dir = Path(capture_dir)
    output_dir = Path(output_dir) if output_dir is not None else capture_dir / "aligned"
    output_dir.mkdir(parents=True, exist_ok=True)

    meta = load_meta(capture_dir)
    rgb = load_rgb(capture_dir)
    rgb_meta = meta["rgb"]
    depth_meta = meta["depth"]
    depth_w = int(depth_meta["resolution_w"])
    depth_h = int(depth_meta["resolution_h"])

    raw_depth = load_depth_raw(capture_dir, depth_w, depth_h)
    depth_m = raw_depth_to_linear_m(raw_depth, depth_meta)
    valid = np.isfinite(depth_m) & (depth_m >= min_depth) & (depth_m <= max_depth)

    points_world = unproject_depth_to_world(depth_m, depth_meta, valid)
    points_rgb = world_to_rgb_camera(points_world, rgb_meta)
    in_front = points_rgb[:, 2] > 0.01
    points_rgb = points_rgb[in_front]

    u, v = project_rgb(points_rgb, rgb_meta)
    rgb_h, rgb_w = rgb.shape[:2]
    in_bounds = (u >= 0) & (u < rgb_w) & (v >= 0) & (v < rgb_h)
    points_rgb = points_rgb[in_bounds]
    u = u[in_bounds]
    v = v[in_bounds]

    aligned_depth = np.full((rgb_h, rgb_w), np.inf, dtype=np.float32)
    np.minimum.at(aligned_depth, (v, u), points_rgb[:, 2].astype(np.float32))
    aligned_depth[~np.isfinite(aligned_depth)] = 0.0

    # Color point cloud from the same RGB pixels used by alignment.
    point_colors = rgb[v, u].astype(np.uint8)

    overlay = make_overlay(rgb, aligned_depth, min_depth, max_depth)
    np.save(output_dir / "aligned_depth_m.npy", aligned_depth)
    np.save(output_dir / "point_cloud_rgb_camera_m.npy", points_rgb.astype(np.float32))
    cv2.imwrite(str(output_dir / "aligned_overlay.png"), cv2.cvtColor(overlay, cv2.COLOR_RGB2BGR))

    depth_vis = np.zeros_like(aligned_depth, dtype=np.uint8)
    valid_aligned = aligned_depth > 0
    if valid_aligned.any():
        lo = max(min_depth, float(np.percentile(aligned_depth[valid_aligned], 1)))
        hi = min(max_depth, float(np.percentile(aligned_depth[valid_aligned], 99)))
        depth_vis[valid_aligned] = np.clip((aligned_depth[valid_aligned] - lo) / max(hi - lo, 1e-6) * 255, 0, 255).astype(np.uint8)
    depth_png = cv2.applyColorMap(depth_vis, cv2.COLORMAP_TURBO)
    depth_png[~valid_aligned] = (0, 0, 0)
    cv2.imwrite(str(output_dir / "aligned_depth_colormap.png"), depth_png)
    write_ascii_ply(output_dir / "point_cloud_rgb_camera.ply", points_rgb.astype(np.float32), point_colors)

    summary = {
        "capture_dir": str(capture_dir),
        "output_dir": str(output_dir),
        "rgb_resolution": [rgb_w, rgb_h],
        "depth_resolution": [depth_w, depth_h],
        "valid_depth_samples": int(np.count_nonzero(valid)),
        "projected_pixels": int(np.count_nonzero(aligned_depth > 0)),
        "point_cloud_points": int(points_rgb.shape[0]),
        "depth_units": "metres",
        "point_cloud_coordinates": "RGB camera space: x right, y up, z forward, units metres",
    }

    # ── Dense depth completion (optional) ────────────────────
    completed_depth_path = output_dir / "dense_depth_any2full.npy"
    if complete_depth:
        if any2full_dir is None or any2full_venv_python is None:
            raise Any2FullConfigurationError(ANY2FULL_SETUP_MESSAGE)
        print("Running Any2Full depth completion...", flush=True)
        run_any2full_completion(
            rgb_path=capture_dir / "rgb.jpg",
            sparse_depth_path=output_dir / "aligned_depth_m.npy",
            output_path=completed_depth_path,
            any2full_dir=Path(any2full_dir),
            any2full_venv_python=str(any2full_venv_python),
            checkpoint_path=any2full_checkpoint,
            encoder=any2full_encoder,
        )
        dense = np.load(completed_depth_path)
        summary["dense_depth_completion"] = {
            "method": f"Any2Full-{any2full_encoder}",
            "resolution": list(dense.shape),
            "depth_range": [float(np.min(dense)), float(np.max(dense))],
        }
    (output_dir / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return summary


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Align one Quest 3 RGB-D capture directory.")
    parser.add_argument("capture_dir", type=Path)
    parser.add_argument("--output-dir", type=Path, default=None)
    parser.add_argument("--min-depth", type=float, default=0.2)
    parser.add_argument("--max-depth", type=float, default=8.0)
    parser.add_argument("--complete-depth", action="store_true",
                        help="Run Any2Full dense depth completion after alignment")
    parser.add_argument("--any2full-dir", type=Path,
                        default=None,
                        help="Path to Any2Full repo directory (required with --complete-depth)")
    parser.add_argument("--any2full-venv-python", type=Path,
                        default=None,
                        help="Path to Any2Full Python executable (required with --complete-depth)")
    parser.add_argument("--any2full-checkpoint", type=str, default=None,
                        help="Any2Full checkpoint path (default: <any2full-dir>/checkpoints/Any2Full_vitl.pth.tar)")
    parser.add_argument("--any2full-encoder", type=str, default="vitl",
                        choices=["vits", "vitb", "vitl"],
                        help="Any2Full encoder variant")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    try:
        summary = align_rgbd_capture(
            args.capture_dir, args.output_dir, args.min_depth, args.max_depth,
            complete_depth=args.complete_depth,
            any2full_dir=args.any2full_dir,
            any2full_venv_python=args.any2full_venv_python,
            any2full_checkpoint=args.any2full_checkpoint,
            any2full_encoder=args.any2full_encoder,
        )
    except Any2FullConfigurationError as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(2) from None
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
