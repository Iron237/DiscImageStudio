# 全彩工作流（实验）

本 fork 在 DIS 原生 WPF 工程流程中加入「全彩」：设计 → 全彩 → 导出 → 刻录。全彩直接继承当前设计页的完整合成结果，保留全部可见图层的叠加顺序、位置、缩放和旋转。页面提供模型预览、参数编辑、方案保存、64 纹理标定计划和完整 BD-R ISO 导出。

**数据转换已通过 DIS v1.5.0 原始编码器交叉检查；实盘全彩未验证。** 当前仅支持单层、全新未格式化、完整容量的威宝 64099 BD-R。几何由 DIS 的 `bd-r-verbatim-64099` 预设读取，不会为适应容量而缩小几何。

## 使用

1. 打开采用威宝 64099 几何的 BD 工程，在设计页完成图层排版。
2. 点「下一步：全彩」或侧边栏「全彩」。来源自动取设计页的 8192 px 彩色合成图，不另选图片，也不只取某一个图层。全彩拟合位于普通黑白量化之前。
3. 编辑右侧绘图区、颜色采样步长、微单元步长、光源和观察位置、曝光、展宽与背景参数。坐标单位 mm，以盘心为原点，数据面朝观察者。
4. 点「生成预览」。左侧显示参与求解的采样目标和预测颜色；展开诊断可看原图和 XYZ 残差。残差不是实拍色差或 CIE ΔE。
5. 修改设计图层、位置、缩放、旋转、显隐、叠放顺序、盘片几何、全彩参数或实测色表后，旧计划不能导出或刻录；重新生成预览后恢复。每次生成使用新的时间戳目录，旧计划不覆盖。
6. 查看预览后，如使用未实测模型，勾选实验输出选项，再点「导出完整 ISO」。完整镜像为 25,025,314,816 bytes，生成后自动做软件长度和簇采样校验；进度、日志、取消均使用 DIS 的任务栏。

有效的全彩图片预览会选中「导出与刻录使用当前全彩结果」。随后导出页消费该计划，不再重新走黑白转换。刻录页读取已导出并校验的完整全彩 ISO，保留原有刻录确认、速度和容量检查，不允许截短几何；尚未导出或设计已变更时会阻止使用旧镜像。也可取消该选项，返回原来的普通绘图链。

「保存方案」/「载入方案」读写 `.bdcolor.json`，只保存实测表、绘图和光学参数；图像来源始终属于当前工程设计。Ctrl+S 在全彩页面保存该参数方案。载入方案不会执行其中的程序路径；Python 运行环境由当前安装或用户在高级设置中选择。

「打开计划」只接受设计指纹与当前工程匹配的 `plan.json`，恢复参数并加载已有的预测图。指纹涵盖设计布局、图层文件、几何和工程路径，旧的独立选图计划不能冒充当前设计。导出器另会核验网格、纹理元数据和光路签名。未完成文件保留为 `.partial`；选中同一 ISO 输出路径可按检查点续算，必须使用相同计划。

## 先做实盘标定

点「64 纹理标定计划」，检查输出的 `patch_key.png` 和 `patch_manifest.json`，再导出测试镜像。固定相机、光源与盘片，用统一标尺记录每个色块的 XYZ；填写 `measurements_template.json` 的副本，在右侧选择该实测表后重新转换图片。空模板不代表测量结果。

默认绘图区为盘面上方 20×12 mm、80×48 个颜色求解点。该矩形按设计页的毫米坐标从完整合成图中采样，保留设计布局；当前版本不会自动覆盖整个记录环。矩形外为普通背景用户数据。相机约 (0,43,400)，白光源约 (453.5574,43,400)。这些是模型初值，需用这批盘片、光驱和光路验证。当前模型未完整模拟材料反射、二维轨道衍射和真实刻录机 DC 策略，不承诺覆盖 sRGB 或任意角度全彩。随盘文件夹非空时会阻止全彩生成，避免丢弃文件。

输出为无 ISO9660/UDF 文件系统的纯绘图用户扇区流。刻录页会直接写入当前已导出的全彩镜像；也可交给外部软件按“将镜像写入光盘”处理，不能把 ISO 当普通文件重新打包。普通 DIS 的 AA/44 灰度读回预览不能解释这些彩色纹理，因此任务栏不会把全彩 ISO 送入普通读回预览。全彩参数页面本身只生成文件。

## 构建与部署

```powershell
dotnet publish src/DiscImageStudio.App/DiscImageStudio.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/fullcolor
./tools/setup-fullcolor.ps1 -AppDirectory artifacts/fullcolor
./artifacts/fullcolor/DiscImageStudio.exe
```

主程序及界面仍为 DIS 原生 WPF。数值后端来自此前核验的 BDColorLab 64099 v0.1，源码在 `src/DiscImageStudio.FullColor.Engine/`，随应用复制到 `FullColorEngine/`。使用独立 Python 3.10+ 环境，依赖 NumPy、Pillow、SciPy；安装脚本建立 `FullColorRuntime/`，不改系统 Python。高级设置可改选已有环境，也可在启动前设置 `DIS_FULLCOLOR_PYTHON`。

源代码不提交生成的二进制色谱库：首次预览在工作目录构建并缓存，随后复用。转换和导出不联网。参数快照通过 JSON 文件交给独立进程；取消会终止该进程树，未完成镜像不会当作成功结果发布。

## 验证

```powershell
dotnet build DiscImageStudio.slnx -c Release
dotnet run --project src/DvdImageSolver -c Release -- selftest
dotnet run --project tests/DiscImageStudio.ArchitectureTests -c Release
python -m unittest discover -s src/DiscImageStudio.FullColor.Engine/tests -v
```

`--test-fullcolor` 可单独运行原生工作流回归。设置 `DIS_TEST_FULLCOLOR_PYTHON` 为依赖齐全的 Python 路径后，还会验证设计合成到数值后端的完整调用；再设 `DIS_TEST_FULLCOLOR_FULL_ISO=1` 则通过原生导出页实际生成完整 ISO，并检查设计变化后的刻录门禁，需至少 26 GB 空闲空间。截图命令 `ui-snapshot --tab 7 --project <工程.discproj>` 支持全彩页，并可用 `--preview <plan.json>` 加载属于该设计的结果。测试涵盖物理位置采样、布局变化失效、非有限数值、任务占用、中文路径、色谱缓存、标定、反扰码校验及禁止覆盖。软件测试不替代实体光盘或颜色测量。
