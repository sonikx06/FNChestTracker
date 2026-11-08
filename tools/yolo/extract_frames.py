"""Extracts frames from your own gameplay recordings for labeling.

Usage:
    python extract_frames.py recording.mp4 out_dir --every 0.25

Only reads video files you recorded yourself (screen recordings). It does not touch the game.
"""
import argparse
import pathlib

import cv2


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("video", type=pathlib.Path)
    parser.add_argument("out", type=pathlib.Path)
    parser.add_argument("--every", type=float, default=0.25, help="seconds between saved frames (default 0.25)")
    parser.add_argument("--start", type=float, default=0.0, help="start time in seconds")
    parser.add_argument("--end", type=float, default=None, help="end time in seconds")
    args = parser.parse_args()

    cap = cv2.VideoCapture(str(args.video))
    if not cap.isOpened():
        raise SystemExit(f"cannot open {args.video}")

    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    step = max(1, round(args.every * fps))
    args.out.mkdir(parents=True, exist_ok=True)
    cap.set(cv2.CAP_PROP_POS_MSEC, args.start * 1000)

    saved = 0
    index = int(args.start * fps)
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        t = index / fps
        if args.end is not None and t > args.end:
            break
        if index % step == 0:
            name = args.out / f"{args.video.stem}_{t:09.3f}.jpg"
            cv2.imwrite(str(name), frame, [cv2.IMWRITE_JPEG_QUALITY, 95])
            saved += 1
        index += 1

    print(f"saved {saved} frames to {args.out}")


if __name__ == "__main__":
    main()
