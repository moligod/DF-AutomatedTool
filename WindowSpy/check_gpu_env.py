import sys
import io
import os
import subprocess
import importlib.util

# 强制 stdout 使用 utf-8
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8')
elif sys.version_info >= (3, 7):
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')

def check_environment():
    print("=== 环境与 DirectML 支持检测工具 ===\n")
    
    # 1. 检测 Python 版本
    py_ver = sys.version.split()[0]
    print(f"Python 版本: {py_ver}")
    
    # 2. 检测 onnxruntime 及版本
    ort_ver = "未安装"
    ort_dml_ver = "未安装"
    ort_spec = importlib.util.find_spec("onnxruntime")
    
    if ort_spec:
        import onnxruntime
        ort_ver = onnxruntime.__version__
        try:
            # 检查 pip list 确认 onnxruntime-directml
            result = subprocess.run([sys.executable, '-m', 'pip', 'list'], 
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8')
            if result.returncode == 0:
                for line in result.stdout.splitlines():
                    if 'onnxruntime-directml' in line:
                        ort_dml_ver = line.split()[1]
                        break
        except:
            pass

    print(f"onnxruntime 版本: {ort_ver}")
    print(f"onnxruntime-directml 版本: {ort_dml_ver}")
    
    # 3. 检测可用 Providers
    if ort_spec:
        import onnxruntime
        available_providers = onnxruntime.get_available_providers()
        print(f"当前支持的 Providers: {', '.join(available_providers)}")
        
        if 'DmlExecutionProvider' in available_providers:
            print("\n[√] DirectML 加速已就绪 (支持 AMD/Intel/NVIDIA 显卡)")
        else:
            print("\n[!] 未检测到 DirectML 支持，将使用 CPU 模式")
    else:
        print("\n[X] onnxruntime 未安装")

if __name__ == "__main__":
    try:
        check_environment()
    except Exception as e:
        print(f"\n[X] 检测过程中发生错误: {e}")
