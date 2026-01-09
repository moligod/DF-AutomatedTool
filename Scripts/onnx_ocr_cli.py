import argparse
import json
import sys
import os
import time
import base64
import numpy as np
import cv2

def process_image(model, img_input):
    if isinstance(img_input, str):
        img = cv2.imread(img_input)
        if img is None:
            # Try to decode path, prevent failure due to path encoding issues
            try:
                img = cv2.imdecode(np.fromfile(img_input, dtype=np.uint8), cv2.IMREAD_COLOR)
            except Exception:
                pass
        
        if img is None:
            return {"error": f"cv2.imread failed: {img_input}"}
    elif isinstance(img_input, np.ndarray):
        img = img_input
    else:
        return {"error": "Invalid image input type"}
    
    try:
        start_time = time.time()
        result = model.ocr(img, det=True, rec=True, cls=model.use_angle_cls)
        end_time = time.time()
        
        # Log inference time to stderr so it shows up in C# console
        sys.stderr.write(f"[Python Info] OCR Inference Time: {(end_time - start_time):.4f}s\n")
        sys.stderr.flush()

        # result is usually a list of results (one per image if batch, but here batch=1)
        # PaddleOCR/OnnxOCR structure: [ [ [box, [text, score]], ... ] ]
        if not result or result[0] is None:
             return []

        out = []
        for line in result[0]:
            box = line[0]
            rec = line[1]
            
            # Convert to native types to avoid numpy serialization errors
            native_box = []
            try:
                for pt in box:
                    native_box.append([float(pt[0]), float(pt[1])])
            except:
                # Fallback if box structure is unexpected
                native_box = [[0.0, 0.0], [0.0, 0.0], [0.0, 0.0], [0.0, 0.0]]

            out.append({
                "text": str(rec[0]),
                "score": float(rec[1]),
                "box": native_box
            })
        return out
    except Exception as e:
        sys.stderr.write(f"[Python Error] process_image failed: {e}\n")
        sys.stderr.flush()
        return {"error": str(e)}

def main():
    # ... (args parsing) ...
    parser = argparse.ArgumentParser()
    parser.add_argument("--onnx_root", type=str, required=True)
    parser.add_argument("--image", type=str, help="Image path for single run mode")
    parser.add_argument("--use_angle_cls", type=str, default="true")
    parser.add_argument("--det_model_dir", type=str, help="Path to det model")
    parser.add_argument("--rec_model_dir", type=str, help="Path to rec model")
    parser.add_argument("--rec_char_dict_path", type=str, help="Path to rec char dict")
    parser.add_argument("--interactive", action="store_true", help="Run in interactive mode (stdin/stdout)")
    args = parser.parse_args()

    # ... (rest of init) ...
    onnx_root = args.onnx_root
    use_angle_cls = args.use_angle_cls.lower() in ("true", "1", "t")

    if not os.path.isdir(onnx_root):
        print(json.dumps({"error": f"onnx_root not found: {onnx_root}"}))
        return 1
        
    # import package
    sys.path.insert(0, onnx_root)
    try:
        from onnxocr.onnx_paddleocr import ONNXPaddleOcr
    except Exception as e:
        print(json.dumps({"error": f"import failed: {e}"}))
        return 1

    # Initialize model
    try:
        # Check environment for GPU flag, default False
        use_gpu = os.environ.get("ONNX_USE_GPU", "0") == "1"
        
        # Prepare kwargs for ONNXPaddleOcr
        kwargs = {
            "use_angle_cls": use_angle_cls,
            "use_gpu": use_gpu,
            "rec_batch_num": 1 # Reduce batch size to 1 to minimize memory usage and complexity
        }
        if args.det_model_dir:
            kwargs["det_model_dir"] = args.det_model_dir
        if args.rec_model_dir:
            kwargs["rec_model_dir"] = args.rec_model_dir
        if args.rec_char_dict_path:
            kwargs["rec_char_dict_path"] = args.rec_char_dict_path

        model = ONNXPaddleOcr(**kwargs)
        
        # 验证是否真正启用了 GPU
        try:
            # 检查 det 模型 session 的 providers
            if hasattr(model, 'text_detector') and hasattr(model.text_detector, 'det_onnx_session'):
                providers = model.text_detector.det_onnx_session.get_providers()
                sys.stderr.write(f"[Python Info] TextDetector Providers: {providers}\n")
                if 'DmlExecutionProvider' not in providers and use_gpu:
                     sys.stderr.write(f"[Python Warning] DirectML was requested but TextDetector is using: {providers}\n")
            
            # 检查 rec 模型 session 的 providers
            if hasattr(model, 'text_recognizer') and hasattr(model.text_recognizer, 'rec_onnx_session'):
                providers = model.text_recognizer.rec_onnx_session.get_providers()
                sys.stderr.write(f"[Python Info] TextRecognizer Providers: {providers}\n")
                if 'DmlExecutionProvider' not in providers and use_gpu:
                     sys.stderr.write(f"[Python Warning] DirectML was requested but TextRecognizer is using: {providers}\n")
                     
        except Exception as e:
            sys.stderr.write(f"[Python Warning] Failed to inspect model providers: {e}\n")

        # GPU Warmup
        if use_gpu:
            sys.stderr.write("[Python Info] Performing GPU Warmup (100x100 black image)...\n")
            sys.stderr.flush()
            warmup_img = np.zeros((100, 100, 3), dtype=np.uint8)
            try:
                model.ocr(warmup_img, det=True, rec=True, cls=use_angle_cls)
                sys.stderr.write("[Python Info] GPU Warmup Completed.\n")
                sys.stderr.flush()
            except Exception as e:
                sys.stderr.write(f"[Python Warning] GPU Warmup Failed: {e}\n")
                sys.stderr.flush()

    except Exception as e:
        print(json.dumps({"error": f"Model init failed: {e}"}))
        return 1

    if args.interactive:
        # Interactive mode: Read paths from stdin
        # Print "READY" to signal C# that initialization is done
        print("READY")
        sys.stdout.flush()

        while True:
            try:
                line = sys.stdin.readline()
                if not line:
                    break
                line = line.strip()
                if not line or line == "EXIT":
                    break
                
                if line.startswith("base64:"):
                    try:
                        b64_data = line[7:]
                        img_bytes = base64.b64decode(b64_data)
                        nparr = np.frombuffer(img_bytes, np.uint8)
                        img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)
                        if img is None:
                             result = {"error": "Failed to decode base64 image"}
                        else:
                             result = process_image(model, img)
                    except Exception as e:
                        result = {"error": f"Base64 decode error: {e}"}
                else:
                    result = process_image(model, line)
                
                # Debug info
                sys.stderr.write("[Python Info] Result processed, encoding JSON...\n")
                sys.stderr.flush()
                
                json_str = json.dumps(result, ensure_ascii=False)
                
                sys.stderr.write("[Python Info] Sending JSON to stdout...\n")
                sys.stderr.flush()
                
                print(json_str)
                sys.stdout.flush()
                
            except Exception as e:
                sys.stderr.write(f"[Python Error] Loop exception: {e}\n")
                sys.stderr.flush()
                print(json.dumps({"error": f"Loop error: {e}"}))
                sys.stdout.flush()
    else:
        # Single run mode
        if not args.image:
            print(json.dumps({"error": "No image provided and not in interactive mode"}))
            return 1
        
        result = process_image(model, args.image)
        print(json.dumps(result, ensure_ascii=False))

    return 0

if __name__ == "__main__":
    sys.exit(main())
