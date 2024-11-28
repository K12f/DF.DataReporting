using DF.Model.Ocr;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;

namespace DF.DataReporting.Helper;

/**
 * 识别逻辑
 * 截图->根据文字找到像素点（find）->模拟点击simulator->点击后显示右边内容区域 (设置每个内容区域的滚轮数)->ocr 统计数据->下一个循环
 */
public class PaddleOcrHelper
{
    // 要识别的文字

    private PaddleOcrAll? _paddleOcrAll;

    private FullOcrModel? _paddleOcrModel;

    private static bool _isInit;

    private async Task InitAsync()
    {
        if (!_isInit)
        {
            _paddleOcrModel ??= await OnlineFullModels.ChineseV4.DownloadAsync();

            _paddleOcrAll ??= new PaddleOcrAll(_paddleOcrModel, PaddleDevice.Onnx())
            {
                AllowRotateDetection = true, /* 允许识别有角度的文字 */
                Enable180Classification = false /* 允许识别旋转角度大于90度的文字 */
            };
            _isInit = true;
        }
    }

    public async Task<PaddleOcrResult> OcrAsync(Mat src)
    {
        await InitAsync();
        var dst = new Mat();
        Cv2.CvtColor(src, dst, ColorConversionCodes.BGRA2BGR);
        return _paddleOcrAll!.Run(dst);
    }
}