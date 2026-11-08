# YOLOv8 chest model (experimental)

The app can count chests with a YOLOv8 object detector instead of the gold-burst heuristic
(**Detection → Detection engine → YOLOv8 model**). **No trained model ships with the app.** You have to
label your own footage and train one with the scripts here. Until then the YOLO engine reports
"Model file not found" and nothing changes: manual counting keeps working.

Everything runs on screen pixels only. Nothing reads Fortnite memory, injects into the game or
interacts with Easy Anti-Cheat.

## Classes

| id | name              | label it when…                                                              |
|----|-------------------|-----------------------------------------------------------------------------|
| 0  | `chest_closed`    | a treasure chest with the lid closed (glowing or not)                       |
| 1  | `chest_opening`   | the lid is lifting, the light burst / loot pop is visible                   |
| 2  | `chest_opened`    | the lid is fully open, the chest is empty or loot lies around it            |
| 3  | `ammo_box`        | ammo boxes (so they are never confused with chests)                         |
| 4  | `other_container` | coolers, safes, supply drops, other searchables                             |

The app reads class names from the ONNX metadata (or `labels.txt`) and maps them to roles. Only
`chest_*` classes can lead to a count. `ammo_box` / `other_container` are only shown in the debug view.

## Counting rules (state machine in `src/ChestTracker.Vision/ChestTracking.cs`)

`Closed → Opening → Opened → Counted`, per tracked chest:

- A track must be seen **closed** first (`RequireClosedBeforeOpening`). A chest that is already open when it
  comes into view is never counted.
- **Opening** must be seen in at least 2 frames, then **opened** in at least 2 frames.
- Seen closed, then open, without a captured opening frame: only queued for review, never counted directly.
- A chest that **disappears** is dropped. Disappearance alone never counts.
- Each track counts once. A new track at the place of a chest counted in the last 10 s inherits "counted",
  so a lost and re-found track cannot count the same chest twice.
- The confidence of an opening = mean confidence of its opening/opened frames. Above the confirm threshold it
  counts. Between review and confirm it goes to the review queue on the dashboard. Below review it is discarded.
- The detection log shows the evidence: frames per state, mean confidences and the state path.
- Wrong counts are fixed as before: **−1** (undo) or reject in the review queue.

Use 6–10 FPS sampling with this engine (Detection → frames per second) so the opening animation is caught.

## 1. Record and extract frames

Record your own matches (e.g. Xbox Game Bar), then:

```bash
pip install -r requirements.txt
python extract_frames.py my_match.mp4 data/raw --every 0.25
```

Label the frames in YOLO format (CVAT, Label Studio or Roboflow), using the class ids above. Put about 80% in
`data/images/train` + `data/labels/train` and 20% in `.../val`. Split by **recording**, not by frame,
otherwise validation scores are inflated by near-identical neighbouring frames.

Aim for at least several hundred boxes per chest class. Include many negatives: ammo boxes, coolers, golden
weapons, gold-coloured walls, the storm, night maps, different graphics settings and resolutions.

## 2. Train and export

```bash
python train.py --data dataset.yaml --model yolov8n.pt --epochs 100
```

Copy `export/chest-yolov8.onnx` and `export/labels.txt` to `models/` next to `FortniteChestTracker.exe`
(or pick the file in the app).

## 3. Evaluate on real gameplay footage

Write down when every chest was opened in a recording you did **not** train on (`events.txt`, one time per
line, seconds or `mm:ss.f`), then:

```bash
dotnet run --project tools/DetectionEval -c Release -- video --model export/chest-yolov8.onnx --video test_match.mp4 --events events.txt --fps 8
dotnet run --project tools/DetectionEval -c Release -- frames --model export/chest-yolov8.onnx --images data/images/val --labels data/labels/val
```

`video` runs the full app pipeline (detector + tracker + state machine) and prints **precision, recall and
false positives** of the counted openings, with the time and evidence of each false positive and miss.
`frames` prints per-class detection precision/recall at IoU 0.5.
