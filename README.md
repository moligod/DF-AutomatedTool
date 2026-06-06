# MoliGod 自动化助手 (DF-AutomatedTool)

一个基于 .NET 8 WPF、OpenCvSharp 和 OnnxRuntime 构建的强大 Windows 桌面自动化工具。

## 简介

本项目旨在提供一个灵活、可视化的 Windows 自动化解决方案。通过集成窗口句柄探测、图像识别（模板匹配）、OCR 文字识别（支持 GPU 加速）以及逻辑控制流（循环、条件判断），用户可以轻松创建复杂的自动化脚本。

## 核心功能

*   **窗口探测 (WindowSpy)**
    *   支持拖拽绑定目标窗口句柄。
    *   自动获取窗口标题、类名及位置信息。
*   **可视化自动化步骤**
    *   **鼠标操作**：支持左键/右键点击，支持随机偏移和随机延迟，模拟真实操作。
    *   **键盘输入**：支持发送按键和文本。
    *   **图像识别**：基于 OpenCV 的模板匹配，支持设定阈值。
    *   **OCR 文字识别**：
        *   集成 OnnxRuntime (PaddleOCR 模型)。
        *   **GPU 加速**：支持 DirectML (AMD/Intel/NVIDIA) 硬件加速。
        *   **性能优化**：支持 ROI (感兴趣区域) 缓存机制，画面未变动时直接复用结果。
*   **强大的逻辑控制**
    *   **流程控制**：支持 Loop 循环（无限/计数）、Break 跳出。
    *   **条件判断**：支持 If / ElseIf / Else / EndIf 结构。
    *   **表达式引擎**：支持复杂的变量比较（如 `A1 > 100 && B1 == "OK"`）。
    *   **预编译优化**：脚本运行前自动构建跳转表，实现 O(1) 复杂度的逻辑跳转。
*   **安全机制**
    *   防卡死设计：5秒内连续右键10次可强制停止脚本。
    *   线程安全：UI 与执行逻辑分离，保证界面响应。

## 环境要求

*   **操作系统**：Windows 10 / 11 (64-bit)
*   **运行环境**：
    *   [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或 Runtime。
    *   [Python 3.10+](https://www.python.org/) (用于 OCR 推理后端)。
*   **Python 依赖**：
    请在项目根目录运行以下命令安装依赖：
    ```bash
    pip install -r requirements.txt
    ```

## 快速开始

1.  **克隆仓库**
    ```bash
    git clone git@github.com:moligod/DF-AutomatedTool.git
    cd DF-AutomatedTool
    ```

2.  **编译项目**
    ```bash
    dotnet build -c Release
    ```

3.  **运行**
    运行生成的 `WindowSpy.exe`。

4.  **使用流程**
    *   **绑定窗口**：使用“绑定窗口A/B”的拖拽图标，拖动到目标窗口释放。
    *   **添加步骤**：在左侧面板配置点击位置、图像识别区域或 OCR 区域，点击“添加”按钮加入队列。
    *   **逻辑编排**：使用 If/Loop 等按钮添加控制流。
    *   **编辑步骤**：右键点击步骤列表中的项，可进行详细参数修改（如开启“ROI未变复用”）。
    *   **保存/加载**：支持将步骤队列保存为 JSON 文件。
    *   **执行**：点击“运行脚本”开始自动化任务。

## 发电地址
爱发电主页：https://ifdian.net/a/moligod
## 免责声明

本软件开源免费仅供学习交流，**请勿用于非法用途以及商业用途！**
作者不对使用本软件产生的任何后果负责。

## 许可证

GPL License
