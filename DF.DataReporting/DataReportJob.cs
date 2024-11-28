using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Models;
using DF.DataReporting.Helper;
using DF.Model.Ocr;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Quartz;
using Sdcb.PaddleOCR;
using Serilog;
using Serilog.Core;
using OpenCvSharpPoint = OpenCvSharp.Point;


namespace DF.DataReporting;

public class DataReportJob : IJob
{
    private static readonly PaddleOcrHelper paddleOcrHelper = new();

    private static readonly Logger Logger = new LoggerConfiguration()
        .WriteTo.Console()
        .WriteTo.File(
            "log.txt", rollingInterval: RollingInterval.Day, encoding: Encoding.UTF8
        )
        .CreateLogger();


    private static readonly MongoClient
        MongoClient = new("mongodb://root:yourpassword@localhost:27017");

    private static readonly AdbClient AdbClient = new();

    private static readonly IMongoDatabase MongoDatabase = MongoClient.GetDatabase("delta_force");

    public async Task Execute(IJobExecutionContext context)
    {
        try
        {
            var itemErrorList = new List<BsonDocument>();

            var categoryList = new List<string>
            {
                "枪械",
                "装备",
                "配件",
                "弹药",
                "收集品",
                "消耗品",
                "钥匙"
            };
            foreach (var category in categoryList)
            {
                // 数据
                var filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("primaryClassCN", category),
                    Builders<BsonDocument>.Filter.Eq("show", true)
                );
                var itemsCollection = MongoDatabase.GetCollection<BsonDocument>("items_v2");
                var items = await itemsCollection.Find(filter)
                    .ToListAsync();

                itemErrorList = await Run(items);
            }

            foreach (var itemError in itemErrorList)
                Logger.Error("{itemName} 识别失败", itemError.GetValue("objectName").AsString);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "程序异常");
        }
    }

    private static async Task<List<BsonDocument>> Run(List<BsonDocument> items)
    {
        var itemPriceCollection = MongoDatabase.GetCollection<BsonDocument>("item_price_v2");

        var device = (await AdbClient.GetDevicesAsync()).FirstOrDefault(); // Get first connected device
        var deviceClient = new DeviceClient(AdbClient, device);
        var imageFrameBuffer = await AdbClient.GetFrameBufferAsync(device, CancellationToken.None);

        var itemsErrorList = new List<BsonDocument>();


        using var backIconMat = Cv2.ImRead("./Assets/image/back.png");
        // 价格详情页
        using var backForSellIconMat = Cv2.ImRead("./Assets/image/back-forsell.png");

        using var buyIconMat = Cv2.ImRead("./Assets/image/buy.png");

        using var delIconMat = Cv2.ImRead("./Assets/image/del.png");

        Logger.Information("-----检测总数-----: {itemCount}", items.Count);

        var successItemCount = 0;
        foreach (var item in items)
        {
            ClearText(deviceClient);

            // var itemId = item.GetValue("_id");
            var itemName = item.GetValue("objectName").AsString;
            // 游戏bug，输入文本显示不出来数据
            var itemSearchText = item.GetValue("objectSearchText").AsString;
            var itemText = item.GetValue("objectOcrText").AsString;

            var itemObjectId = item.GetValue("objectID").AsInt64;

            Logger.Information("检测分类子项成功数: {itemCount}", successItemCount);
            Logger.Information("共{itemCount},当前第: {subMenuText}个", items.Count, successItemCount);
            Logger.Information("检测分类子项数据: {itemName}", itemName);

            var itemPrice = 0;
            var itemOcrRetry = 3;
            do
            {
                await RandomSleep();
                // 等待图像帧
                var mainMat = await FreshFrameBuffer(imageFrameBuffer);
                // 清空文本
                var delPoint = MatchTemplate(mainMat, delIconMat, 0.8);
                Logger.Information("删除图标位置{delPoint}", delPoint);
                if (delPoint.X == 0)
                {
                    delPoint.X = 815;
                    delPoint.Y = 121;
                    Logger.Information("未找到删除图标,使用固定图标位置{delPoint}", delPoint);
                }

                await RandomClick(deviceClient, delPoint);

                mainMat = await FreshFrameBuffer(imageFrameBuffer);
                // 用来定位输入框
                var buyIconPoint = MatchTemplate(mainMat, buyIconMat, 0.8);
                if (buyIconPoint.X == 0)
                {
                    Logger.Warning("定位搜索框失败");
                    itemOcrRetry--;
                    continue;
                }

                Logger.Information("开始搜索并输入文字");
                // await deviceClient.ClickAsync(buyIconPoint.X + 400, buyIconPoint.Y + 30);

                await RandomClick(deviceClient, new OpenCvSharpPoint(buyIconPoint.X + 380, buyIconPoint.Y + 25));
                await RandomSleep();

                SendText(deviceClient, itemSearchText);

                await RandomSleep();

                mainMat = await FreshFrameBuffer(imageFrameBuffer);

                // 获取图像的一部分
                var srcRoi = new Rect(0, buyIconPoint.Y + 90, mainMat.Width, mainMat.Height - buyIconPoint.Y - 300);

                var croppedSrcImage = new Mat(mainMat, srcRoi);

                // await Debug(croppedSrcImage, new OpenCvSharpPoint());

                var itemOcrResult = paddleOcrHelper.OcrAsync(croppedSrcImage);

                var itemOcrCoordinate = TextSimilarity(itemOcrResult.Result.Regions, itemText);

                var itemOcrCoordinateX = (int)itemOcrCoordinate.Rect.Center.X;
                var itemOcrCoordinateY = (int)itemOcrCoordinate.Rect.Center.Y;

                Logger.Information("ocr识别坐标: {itemOcrCoordinateX},{itemOcrCoordinateY}", itemOcrCoordinateX,
                    itemOcrCoordinateY);

                if (itemOcrCoordinateX == 0 || itemOcrCoordinateY == 0)
                {
                    Logger.Warning("未找到数据 ocr {itemName}", itemName);
                    Logger.Warning("识别坐标失败");
                    itemOcrRetry--;
                    continue;
                }

                Logger.Information("开始点击坐标{x},{y}", itemOcrCoordinateX, itemOcrCoordinateY + buyIconPoint.Y + 90);

                // await deviceClient.ClickAsync(itemOcrCoordinateX, itemOcrCoordinateY + buyIconPoint.Y + 90);

                await RandomClick(deviceClient,
                    new OpenCvSharpPoint(itemOcrCoordinateX, itemOcrCoordinateY + buyIconPoint.Y + 90));

                var priceOcrRetry = 3;
                Logger.Information("开始ocr价格");

                do
                {
                    mainMat = await FreshFrameBuffer(imageFrameBuffer);

                    var cropPriceRegionMat = CropPriceMat(mainMat);

                    var priceOcrResult = await paddleOcrHelper.OcrAsync(cropPriceRegionMat);
                    PaddleOcrResultRegion priceOcr =
                        priceOcrResult.Regions.OrderByDescending(r => r.Score).FirstOrDefault();
                    if (priceOcr.Text is null)
                    {
                        priceOcrRetry--;
                        continue;
                        // priceOcr = priceOcrResult.Regions.OrderByDescending(r => r.Score).FirstOrDefault();
                    }

                    itemPrice = ExtractNumber(priceOcr.Text);

                    Logger.Information("当前数据价格: {itemPrice}", itemPrice);
                    priceOcrRetry--;
                    if (priceOcrRetry == 0)
                    {
                        itemsErrorList.Add(
                            new BsonDocument
                            {
                                { "objectID", itemObjectId },
                                { "objectName", itemName },
                                { "createdAt", DateTime.Now },
                                { "updatedAt", DateTime.Now }
                            }
                        );
                        Logger.Error("未找到当前数据价格 ocr {itemName}", itemName);
                    }
                } while (itemPrice == 0 && priceOcrRetry > 0);

                mainMat = await FreshFrameBuffer(imageFrameBuffer);
                var backForSellPoint = MatchTemplate(mainMat, backIconMat, 0.85);
                var backForSellPointX = backForSellPoint.X;
                var backForSellPointY = backForSellPoint.Y;
                if (backForSellPointX == 0)
                {
                    Logger.Error("未检测到返回按钮");

                    // 返回坐标固定左边
                    Logger.Warning("使用固定坐标点");
                    backForSellPointX = 139;
                    backForSellPointY = 23;
                }

                // await deviceClient.ClickAsync(backForSellPointX, backForSellPointY);
                await RandomClick(deviceClient, backForSellPoint);
                Logger.Information("back");

                successItemCount++;
                itemOcrRetry--;
            } while (itemPrice == 0 && itemOcrRetry > 0);

            await itemPriceCollection.InsertOneAsync(
                new BsonDocument
                {
                    { "objectID", itemObjectId },
                    { "objectName", itemName },
                    { "price", itemPrice },
                    { "createdAt", DateTime.Now },
                    { "updatedAt", DateTime.Now }
                }
            );
        }

        return itemsErrorList;
    }

    /// <summary>
    ///     4 chanel转为 3chanel
    /// </summary>
    /// <param name="src"></param>
    /// <returns></returns>
    private static Mat ConvertTo3Channel(Mat src)
    {
        var dst = new Mat();
        Cv2.CvtColor(src, dst, ColorConversionCodes.BGRA2BGR);
        return dst;
    }

    /// <summary>
    ///     切割价格部分
    /// </summary>
    /// <param name="src"></param>
    /// <returns></returns>
    private static Mat CropPriceMat(Mat src)
    {
        var screenshot2KMenuConfig = new GameCaptureConfig(0.7, 0.85, 0, 0);

        var srcX = screenshot2KMenuConfig.XPressure * src.Width; // 左上角的 x 坐标
        var srcY = screenshot2KMenuConfig.YPressure * src.Height; // 左上角的 x 坐标
        var srcWidth = src.Width - srcX; // 宽度
        var srcHeight = src.Height - srcY; // 宽度
        var srcRoi = new Rect((int)srcX, (int)srcY, (int)srcWidth, (int)srcHeight);

        return new Mat(src, srcRoi);
    }

    /// <summary>
    ///     获取图片中绿色的部分,用来识别价格
    /// </summary>
    /// <param name="src"></param>
    /// <returns></returns>
    private static Mat GetCoinGreenRegion(Mat src)
    {
        // 转换为 HSV 颜色空间
        var hsvImage = new Mat();
        Cv2.CvtColor(src, hsvImage, ColorConversionCodes.BGR2HSV);

        // 定义绿色的 HSV 范围
        var lowerGreen = new Scalar(40, 100, 100); // 低阈值
        var upperGreen = new Scalar(80, 255, 255); // 高阈值

        // 创建掩膜
        var mask = new Mat();
        Cv2.InRange(hsvImage, lowerGreen, upperGreen, mask);

        // 提取绿色边框的部分
        var result = new Mat();
        Cv2.BitwiseAnd(src, src, result, mask);
        return result;
    }

    /// <summary>
    ///     获取图片中红色的部分，用来识别红色的价格
    /// </summary>
    /// <param name="src"></param>
    /// <returns></returns>
    private static Mat GetCoinRedRegion(Mat src)
    {
        // 转换为 HSV 颜色空间
        var hsvImage = new Mat();
        Cv2.CvtColor(src, hsvImage, ColorConversionCodes.BGR2HSV);

        // 定义红色的 HSV 范围
        var lowerRed1 = new Scalar(0, 100, 100); // 红色低阈值范围 1
        var upperRed1 = new Scalar(10, 255, 255); // 红色高阈值范围 1
        var lowerRed2 = new Scalar(170, 100, 100); // 红色低阈值范围 2
        var upperRed2 = new Scalar(180, 255, 255); // 红色高阈值范围 2

        // 创建掩膜
        var mask1 = new Mat();
        var mask2 = new Mat();

        Cv2.InRange(hsvImage, lowerRed1, upperRed1, mask1); // 掩膜 1
        Cv2.InRange(hsvImage, lowerRed2, upperRed2, mask2); // 掩膜 2

        // 合并两个掩膜
        var mask = new Mat();
        Cv2.BitwiseOr(mask1, mask2, mask);

        // 提取红色部分
        var result = new Mat();
        Cv2.BitwiseAnd(src, src, result, mask);
        return result;
    }


    /// <summary>
    ///     计算 Levenshtein 距离
    /// </summary>
    /// <param name="str1"></param>
    /// <param name="str2"></param>
    /// <returns></returns>
    private static double LevenshteinDistance(string str1, string str2)
    {
        var dp = new int[str1.Length + 1, str2.Length + 1];

        for (var i = 0; i <= str1.Length; i++)
        for (var j = 0; j <= str2.Length; j++)
            if (i == 0)
                dp[i, j] = j;
            else if (j == 0)
                dp[i, j] = i;
            else
                dp[i, j] = Math.Min(dp[i - 1, j] + 1, // 删除
                    Math.Min(dp[i, j - 1] + 1, // 插入
                        dp[i - 1, j - 1] + (str1[i - 1] == str2[j - 1] ? 0 : 1))); // 替换

        var distance = dp[str1.Length, str2.Length];
        return 1.0 - (double)distance / Math.Max(str1.Length, str2.Length);
    }


    /// <summary>
    ///     解析文本中的价格
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static int ExtractNumber(string input)
    {
        if (!IsNumber(input)) return 0;

        // 替换逗号和点号（根据您的需求，这里只是替换了逗号）
        input = input.Replace(",", "").Replace(".", "");

        // 使用正则表达式匹配整数和小数
        var pattern = @"\d+(\.\d+)?";
        var match = Regex.Match(input, pattern);

        // 返回找到的第一个数字，如果没有找到则返回 null
        return match.Success ? int.Parse(match.Value) : 0;
    }

    /// <summary>
    ///     判断价格是否是数字
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    private static bool IsNumber(string? input)
    {
        if (input is null) return false;

        var numericPattern = @"^[\d,]+$";
        return Regex.IsMatch(input, numericPattern);
    }

    private static OpenCvSharpPoint MatchTemplate(Mat src, Mat template, double threshold)
    {
        var graySrc = new Mat();
        var grayTemplate = new Mat();
        Cv2.CvtColor(src, graySrc, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(template, grayTemplate, ColorConversionCodes.BGR2GRAY);

        // 创建一个存储匹配结果的图像
        var result = new Mat();
        Cv2.MatchTemplate(graySrc, grayTemplate, result, TemplateMatchModes.CCoeffNormed);

        // 获取最佳匹配的位置
        Cv2.MinMaxLoc(result, out var minVal, out var maxVal, out var minLoc,
            out var maxLoc);

        Logger.Information("maxVal: {maxVal}", maxVal);
        if (maxVal < threshold)
        {
            // // 计算小图的尺寸
            var smallImageSize = template.Size();
            //
            // // 获取匹配的小图的坐标
            var matchedPoint = maxLoc; // 匹配的小图左上角坐标
            //
            // Cv2.ImShow("Green Border", template);

            // // 在大图上绘制矩形以标记小图的位置
            // Cv2.Rectangle(src, matchedPoint,
            //     new OpenCvSharpPoint(matchedPoint.X + smallImageSize.Width, matchedPoint.Y + smallImageSize.Height),
            //     Scalar.Red, 2);

            //        
            // Cv2.ImShow("Green Border", src);
            // Cv2.WaitKey(0);
            // Cv2.DestroyAllWindows();
            // throw new DataReportingException("No match found.");
            return new OpenCvSharpPoint(0, 0);
        }

        return maxLoc;
    }

    /// <summary>
    ///     发送文本
    /// </summary>
    /// <param name="deviceClient"></param>
    /// <param name="text"></param>
    private static void SendText(DeviceClient deviceClient, string text)
    {
        deviceClient.AdbClient.ExecuteRemoteCommand($"am broadcast -a ADB_INPUT_TEXT --es msg '{text}'",
            deviceClient.Device);
    }

    /// <summary>
    ///     清除搜索文本
    /// </summary>
    /// <param name="deviceClient"></param>
    private static void ClearText(DeviceClient deviceClient)
    {
        deviceClient.AdbClient.ExecuteRemoteCommand("am broadcast -a ADB_CLEAR_TEXT",
            deviceClient.Device);
    }

    /// <summary>
    ///     文字相似判断
    /// </summary>
    /// <param name="regions"></param>
    /// <param name="itemText"></param>
    /// <param name="similarity"></param>
    /// <returns></returns>
    private static PaddleOcrResultRegion TextSimilarity(PaddleOcrResultRegion[] regions,
        string itemText, double similarity = 0.7)
    {
        try
        {
            // 验证输入
            if (regions == null || itemText == null) throw new ArgumentException("Input parameters cannot be null");

            var result = regions
                .OrderByDescending(r => r.Score)
                .FirstOrDefault(r => ClearOcrText(r.Text) == ClearOcrText(itemText));

            if (result.Rect.Center.X != 0) return result;

            result = regions
                .OrderByDescending(r => r.Score)
                .FirstOrDefault(r => ClearOcrText(r.Text) == ClearOcrText(itemText + "全新"));

            if (result.Rect.Center.X != 0) return result;

            result = regions
                .OrderByDescending(r => r.Score)
                .FirstOrDefault(r => LevenshteinDistance(ClearOcrText(r.Text),
                    ClearOcrText(itemText)) >= similarity);

            return result;
        }
        catch (Exception ex)
        {
            // 记录日志或处理异常
            Logger.Information("An error occurred: {ex.Message}", ex.Message);
            return new PaddleOcrResultRegion();
        }
    }

    /// <summary>
    ///     清楚ocr中乱七八糟的数据
    /// </summary>
    /// <param name="ocrText"></param>
    /// <returns></returns>
    private static string ClearOcrText(string ocrText)
    {
        return ocrText.Replace(" ", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace("（", "")
            .Replace("）", "")
            .Trim(' ', '\n', ',', '。');
    }

    /// <summary>
    ///     刷新adb屏幕
    /// </summary>
    /// <param name="framebuffer"></param>
    /// <returns></returns>
    private static async Task<Mat> FreshFrameBuffer(Framebuffer framebuffer)
    {
        await RandomSleep();
        await framebuffer.RefreshAsync();
        return framebuffer.ToImage()!.ToMat();
    }

    /// <summary>
    ///     随机sleep
    /// </summary>
    /// <returns></returns>
    private static Task RandomSleep(int min = 1000, int max = 2000)
    {
        var random = new Random();
        var sleepTime = random.Next(min, max);
        Thread.Sleep(sleepTime);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     随机点击，加上偏移量
    /// </summary>
    /// <param name="deviceClient"></param>
    /// <param name="point"></param>
    private static async Task RandomClick(DeviceClient deviceClient, OpenCvSharpPoint point)
    {
        await RandomSleep(500);
        var random = new Random();
        var randomPointX = random.Next(1, 5);
        var randomPointY = random.Next(1, 5);

        await deviceClient.ClickAsync(point.X + randomPointX, point.Y + randomPointY);
    }

    /// <summary>
    ///     debug工具
    /// </summary>
    /// <param name="mainMat"></param>
    /// <param name="checkPoint"></param>
    private static async Task Debug(Mat mainMat, OpenCvSharpPoint checkPoint)
    {
        Cv2.Rectangle(mainMat,
            new Rect(new OpenCvSharpPoint(checkPoint.X, checkPoint.Y),
                new Size(checkPoint.X + 10, checkPoint.Y + 10)), Scalar.Red, 2); // 红色矩形，线宽为2
        Cv2.ImShow("Green Border", mainMat);
        Cv2.WaitKey();
        Cv2.DestroyAllWindows();
    }
}