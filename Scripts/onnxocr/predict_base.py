import onnxruntime

class PredictBase(object):
    def __init__(self):
        pass

    def get_onnx_session(self, model_dir, use_gpu, gpu_id = 0):
        # 使用gpu
        if use_gpu:
            # 优先使用 DirectML，其次 CPU
            providers = [
                ('DmlExecutionProvider', {
                    "device_id": gpu_id,
                }),
                'CPUExecutionProvider'
            ]
        else:
            providers = ['CPUExecutionProvider']

        try:
            # 设置 SessionOptions
            sess_options = onnxruntime.SessionOptions()
            sess_options.log_severity_level = 3
            # 禁用并行优化，可能导致死锁
            # sess_options.intra_op_num_threads = 1
            # sess_options.inter_op_num_threads = 1
            
            onnx_session = onnxruntime.InferenceSession(model_dir, sess_options, providers=providers)
        except Exception as e:
            import sys
            sys.stderr.write(f"Error loading model with requested providers: {e}\n")
            # Fallback to CPU if GPU providers fail
            sys.stderr.write("Falling back to CPUExecutionProvider\n")
            onnx_session = onnxruntime.InferenceSession(model_dir, None, providers=['CPUExecutionProvider'])

        import sys
        sys.stderr.write(f"[Python Init] Model loaded from {model_dir}, providers: {onnx_session.get_providers()}\n")
        return onnx_session


    def get_output_name(self, onnx_session):
        """
        output_name = onnx_session.get_outputs()[0].name
        :param onnx_session:
        :return:
        """
        output_name = []
        for node in onnx_session.get_outputs():
            output_name.append(node.name)
        return output_name

    def get_input_name(self, onnx_session):
        """
        input_name = onnx_session.get_inputs()[0].name
        :param onnx_session:
        :return:
        """
        input_name = []
        for node in onnx_session.get_inputs():
            input_name.append(node.name)
        return input_name

    def get_input_feed(self, input_name, image_numpy):
        """
        input_feed={self.input_name: image_numpy}
        :param input_name:
        :param image_numpy:
        :return:
        """
        input_feed = {}
        for name in input_name:
            input_feed[name] = image_numpy
        return input_feed
