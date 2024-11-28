// See https://aka.ms/new-console-template for more information

using System;
using System.Threading.Tasks;
using DF.DataReporting;
using Quartz;
using Quartz.Impl;

public class Program
{
    // main
    static async Task Main(string[] args)
    {
        //创建一个工作
        IJobDetail job = JobBuilder.Create<DataReportJob>()
            .WithIdentity("DataReport", "DataReport")
            .Build();

        //创建一个触发条件
        ITrigger trigger = TriggerBuilder.Create()
            .WithIdentity("DataReportTrigger", "DataReport")
            .WithSimpleSchedule(x => { x.WithIntervalInHours(8).RepeatForever(); })
            .Build();


        StdSchedulerFactory factory = new StdSchedulerFactory();
        //创建任务调度器
        IScheduler scheduler = await factory.GetScheduler();
        //启动任务调度器
        await scheduler.Start();

        //将创建的任务和触发器条件添加到创建的任务调度器当中
        await scheduler.ScheduleJob(job, trigger);

        // and last shut down the scheduler when you are ready to close your program

        // some sleep to show what's happening
        await Task.Delay(TimeSpan.FromDays(30));
        await scheduler.Shutdown();
    }
}