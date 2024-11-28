namespace DF.Model.Ocr;

public record GameCaptureConfig(
    // x坐标系数
    double XPressure,
    double YPressure,
    double WidthPressure,
    double HeightPressure
);