# Third-party notices

Fortnite Chest Tracker's own source code is licensed under the MIT License (see `LICENSE`).
The following components are distributed with it under their own licenses.

## Chest detection model (`models/best.onnx`)

The built-in chest detection model was trained with [Ultralytics YOLOv8](https://github.com/ultralytics/ultralytics),
starting from the `yolov8n.pt` pretrained weights, and exported to ONNX with Ultralytics 8.4.208. Ultralytics
publishes its software and pretrained models under the
[GNU Affero General Public License v3.0](https://www.gnu.org/licenses/agpl-3.0.html) (AGPL-3.0); the model file's
own metadata states "AGPL-3.0 License (https://ultralytics.com/license)".

The model file is therefore **not** covered by this project's MIT License. It is provided under AGPL-3.0, in
accordance with the Ultralytics license. If you reuse or redistribute the model, follow the AGPL-3.0 terms or obtain
a license from Ultralytics.

The training images were collected by the project author; Fortnite is a trademark of Epic Games, Inc.

## Libraries

| Component | License |
|---|---|
| .NET runtime, ASP.NET Core, SignalR, Microsoft.Data.Sqlite, System.Security.Cryptography.ProtectedData | MIT |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) (Microsoft.ML.OnnxRuntime.DirectML) | MIT |
| [DirectML](https://github.com/microsoft/DirectML) (Microsoft.AI.DirectML, `DirectML.dll`) | Microsoft Software License Terms (redistributable) |
| [OpenCvSharp](https://github.com/shimat/opencvsharp) and the bundled OpenCV runtime | Apache License 2.0 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT |
| [Npgsql](https://github.com/npgsql/npgsql) (server only) | PostgreSQL License |
| SQLite (via Microsoft.Data.Sqlite / SQLitePCLRaw) | Public domain / Apache License 2.0 |

The app icon and the Discord art assets in `assets/discord` were made for this project and are covered by the MIT
License above.
