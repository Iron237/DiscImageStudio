# 来源、许可证与修改说明

本演示的源代码按 GNU General Public License version 3 发布，完整条款见 LICENSE。

## DiscImageStudio

上游：https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio
固定版本：v1.5.0
原作者及贡献者：JiaFeiMiao-K-Cat and DiscImageStudio contributors。

本包 `bdcolor/core.py` 的 17PP 转换、记录帧装配、扰码、名义 LDC/BIS 坐标公式与螺旋模型，是对应上游 C# 实现的 Python/Numpy 移植/改编；测试向量也来自上游测试。不是重新声称这些编码规则由本演示首次提出。具体源码链接见 docs/ALGORITHM.md。

DIS 集成还移植了上游 `DiscImageStudio.Core/ToneProfile.cs` 的 `ToneDither.Phase` 到 `bdcolor/dis_dither.py`，以源像素索引驱动纹理调色板分配；其二值特例与原生 128 阈值逐点交叉验证。原生 WPF 适配器直接调用 `ToneProfile.AdjustedGray` 和 `BluRayImageLoader`，保留 RGB 拟合并另存原生黑白对照；全环采样与色模型仍为本 fork 的实验扩展。

新加入：位置相关 1D 光谱替代模型、多纹理非负颜色拟合、图像计划、图形入口、完整用户扇区导出与断点续算、实测表入口。上游并未为本演示的颜色结论背书。

本包提供完整对应源代码；不包含原项目的 EXE、字体或商业资源。

## 科学方法

CIE XYZ 的近似曲线采用 Wyman, Sloan, Shirley (2013) 论文给出的解析函数，代码为本包实现；不是复用/重发官方 CIE CSV 数据集。

## 运行依赖

NumPy、SciPy、Pillow 由用户安装时从 Python 包源取得，各自遵循其许可证。本包不捆绑这些库或它们的二进制。GUI 依赖 Python 发行版自带的 Tkinter。

`examples/color_chart.png` 是 tools/make_sample.py 生成的软件测试夹具。没有把任何模型预览当成真实盘片照片。
