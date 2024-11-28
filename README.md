# 三角形行动S2赛季 交易行数据监控脚本
> 原理：因为三角形行动手机端与PC端数据是一样的，我们通过手机端搜索物品的功能实现数据抓取

## 使用方法
  - 1.自行准备 MongoDB 数据库
  - 2.将DF.DataReporting/Assets/data/items_v2.json 物品数据导入到MongoDB中,库名delta_force,collection名items_v2
  - 3.修改[MongoDB](https://github.com/K12f/DF.DataReporting/blob/1d69885a8c813a8554f28a83989eabec84a85fa2/DF.DataReporting/DataReportJob.cs#L39)账号密码
  - 4.准备闲置手机一台,打开开发者模式
  - 5.下载[Rider](https://www.jetbrains.com/rider/),导入代码启动Program.cs


## 依赖
- .net8.0
- MongoDB
- PaddleOCR
- adb
- Quartz

## 免责申明⚠️

1. 本软件只是学习、研究、交流，仅供学习交流使用，不得用于任何商业用途。 🚫 **本软件提供的所有内容，仅可用作学习交流使用，未经原作者授权，禁止用于其他用途。请在下载24小时内删除。
   **
2. 因使用本软件产生的版权问题，软件作者概不负责。