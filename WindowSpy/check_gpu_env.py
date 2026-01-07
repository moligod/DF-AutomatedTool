import sys
import importlib.util
import io
import os
import ctypes
import subprocess
import time

# 强制 stdout 使用 utf-8，防止 Windows 下 emoji 报错
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')
elif sys.version_info >= (3, 7):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

def get_cuda_version():
    try:
        # 1. 尝试通过 nvcc (CUDA 编译器) 获取准确的 Runtime 版本
        # 这是最准确的方法，代表实际安装的 CUDA Toolkit 版本
        result = subprocess.run(['nvcc', '--version'], 
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8')
        if result.returncode == 0:
            # 输出通常包含 "release 11.8, V11.8.89" 这样的字样
            import re
            match = re.search(r"release (\d+\.\d+)", result.stdout)
            if match:
                return match.group(1)
        
        # 2. 如果没有 nvcc，尝试检查环境变量 CUDA_PATH
        # 这通常代表 CUDA Toolkit 的安装路径
        cuda_path = os.environ.get('CUDA_PATH')
        if cuda_path:
            # 尝试从路径中解析版本，例如 C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v11.8
            version = os.path.basename(cuda_path.rstrip(os.sep)).replace('v', '')
            return version

        # 3. 尝试读取 cudart64_xx.dll 的版本
        # 这代表程序实际使用的运行时版本
        search_paths = os.environ.get('PATH', '').split(os.pathsep)
        for p in search_paths:
            if not p or not os.path.exists(p): continue
            try:
                files = os.listdir(p)
                for f in files:
                    if f.startswith("cudart64_") and f.endswith(".dll"):
                        # cudart64_110.dll -> 11.0
                        # cudart64_12.dll -> 12.0
                        ver_str = f.replace("cudart64_", "").replace(".dll", "")
                        if len(ver_str) >= 2:
                            return f"{ver_str[:2]}.{ver_str[2:] if len(ver_str)>2 else '0'} (Runtime)"
            except:
                continue

        # 4. 最后才使用 nvidia-smi (这其实是 Driver 版本，但也是一种参考)
        result = subprocess.run(['nvidia-smi', '--query-gpu=driver_version', '--format=csv,noheader'], 
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8')
        if result.returncode == 0:
            return f"{result.stdout.strip()} (驱动版本)"

    except:
        pass
    return "未安装/未检测到"

def get_cudnn_version():
    # 1. 优先进行“实弹演习”：尝试真正加载 CUDA 核心
    # 如果能成功加载，说明 CUDA 和 cuDNN 绝对没问题
    try:
        import onnxruntime
        # 只有当 'CUDAExecutionProvider' 真正出现在可用列表中时
        # 才说明底层的 CUDA/cuDNN 动态库被成功加载到了内存中
        if 'CUDAExecutionProvider' in onnxruntime.get_available_providers():
            return "已安装 (功能验证通过)"
    except:
        pass

    # 2. 如果上面失败了，尝试去硬盘上找文件（仅作参考）
    try:
        search_paths = os.environ.get('PATH', '').split(os.pathsep)
        cuda_path = os.environ.get('CUDA_PATH')
        if cuda_path:
            search_paths.append(os.path.join(cuda_path, 'bin'))
        
        patterns = ["cudnn64_*.dll", "cudnn_cnn_infer64_*.dll"] 
        found_versions = []
        for p in search_paths:
            if not p or not os.path.exists(p): continue
            try:
                files = os.listdir(p)
                for f in files:
                    if f.startswith("cudnn64_") and f.endswith(".dll"):
                        ver = f.replace("cudnn64_", "").replace(".dll", "")
                        found_versions.append(ver)
                    elif f.startswith("cudnn_cnn_infer64_") and f.endswith(".dll"):
                        ver = f.replace("cudnn_cnn_infer64_", "").replace(".dll", "")
                        found_versions.append(ver)
            except:
                continue
                
        if found_versions:
            vers = sorted(list(set(found_versions)), reverse=True)
            return f"检测到文件 v{vers[0]}.x (但未被加载)"
    except:
        pass
        
    return "未安装/未检测到"

def check_environment():
    print("=== 环境与 GPU 支持检测工具 ===\n")
    
    # 1. 检测 Python 版本
    py_ver = sys.version.split()[0]
    print(f"Python 版本: {py_ver}")
    
    # 2. 检测 onnxruntime 及版本
    ort_ver = "未安装"
    ort_gpu_ver = "未安装"
    ort_spec = importlib.util.find_spec("onnxruntime")
    
    if ort_spec:
        import onnxruntime
        ort_ver = onnxruntime.__version__
        # 检查是否安装了 gpu 包 (通常 gpu 包也叫 onnxruntime，但在 pip list 中能看到)
        try:
            # 简单粗暴检查 pip list
            result = subprocess.run([sys.executable, '-m', 'pip', 'list'], 
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8')
            if result.returncode == 0:
                for line in result.stdout.splitlines():
                    if 'onnxruntime-gpu' in line:
                        ort_gpu_ver = line.split()[1]
                        break
        except:
            pass

    print(f"onnxruntime 版本: {ort_ver}")
    print(f"cuda 版本: {get_cuda_version()}")
    print(f"cuDNN 版本: {get_cudnn_version()}")
    print(f"onnxruntime-gpu 版本: {ort_gpu_ver}")
    
    # 3. 检测可用 Providers 及大致速度评估
    providers_info = []
    has_cuda = False
    has_dml = False
    
    if ort_spec:
        import onnxruntime
        available_providers = onnxruntime.get_available_providers()
        
        # 简单的速度基准测试 (仅供参考)
        # 这里的速度评估只是一个文字描述，真实 benchmark 需要跑模型
        for p in available_providers:
            speed_desc = "未知"
            if p == 'TensorrtExecutionProvider':
                speed_desc = "极快"
            elif p == 'CUDAExecutionProvider':
                speed_desc = "快"
                has_cuda = True
            elif p == 'DmlExecutionProvider':
                speed_desc = "较快"
                has_dml = True
            elif p == 'CPUExecutionProvider':
                speed_desc = "慢"
            
            providers_info.append(f"{p}(识别速度：{speed_desc})")
            
        print(f"当前支持的硬件加速识别： {', '.join(providers_info)}")
    else:
        print("当前支持的硬件加速识别： 无 (未安装运行库)")

    print("\n分析结果 ：", end="")
    
    if has_cuda:
        print("您已成功配置GPU显卡加速，程序运行速度将大幅提升。")
    elif has_dml:
        print("您已成功配置DirectML加速(A卡/核显)，程序运行速度将提升。")
    else:
        print("当前仅使用CPU模式。如需提升速度，建议配置GPU环境。")
        print("提示：")
        print(" - 30/40系列显卡建议 CUDA 11.x (兼容性最好)")
        print(" - RTX 50系列显卡必须使用 CUDA 12.x")
        print("   (注意：pip 默认安装的 onnxruntime-gpu 可能仅支持 CUDA 11，")
        print("    RTX 50 用户请使用 DirectML 模式，或手动安装 CUDA 12 版 whl 包)")

if __name__ == "__main__":
    try:
        check_environment()
    except Exception as e:
        print(f"\n[X] 检测过程中发生错误: {e}")
