"""Fine-tunes YOLOv8 on labeled chest frames and exports the model for the app.

Usage:
    python train.py --data dataset.yaml --model yolov8n.pt --epochs 100
Output:
    runs/chest/<name>/weights/best.pt
    export/chest-yolov8.onnx + export/labels.txt  -> copy both into <app>/models/
"""
import argparse
import pathlib
import shutil

from ultralytics import YOLO


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data", default="dataset.yaml")
    parser.add_argument("--model", default="yolov8n.pt", help="pretrained checkpoint to fine-tune (n/s are fast enough on CPU)")
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--imgsz", type=int, default=640)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--name", default="chest")
    parser.add_argument("--device", default=None, help="'0' for the first GPU, 'cpu' to force CPU")
    args = parser.parse_args()

    model = YOLO(args.model)
    model.train(
        data=args.data,
        epochs=args.epochs,
        imgsz=args.imgsz,
        batch=args.batch,
        project="runs/chest",
        name=args.name,
        device=args.device,
        # Fortnite HUD text/minimap must not be mirrored into nonsense; vertical flips never happen in game.
        fliplr=0.0,
        flipud=0.0,
        mosaic=1.0,
        patience=30,
    )

    metrics = model.val(data=args.data, imgsz=args.imgsz)
    print("validation mAP50:", metrics.box.map50, "mAP50-95:", metrics.box.map)

    best = pathlib.Path(model.trainer.best)
    exported = YOLO(str(best)).export(format="onnx", imgsz=args.imgsz, opset=12, simplify=True, dynamic=False)

    out = pathlib.Path("export")
    out.mkdir(exist_ok=True)
    target = out / "chest-yolov8.onnx"
    shutil.copy(exported, target)
    names = YOLO(str(best)).names
    (out / "labels.txt").write_text("\n".join(names[i] for i in sorted(names)) + "\n", encoding="utf-8")
    print(f"exported {target} and {out / 'labels.txt'}")


if __name__ == "__main__":
    main()
