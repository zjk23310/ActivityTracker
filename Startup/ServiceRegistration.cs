using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ActivityTracker.Configuration;
using ActivityTracker.Data;
using ActivityTracker.Services;
using ActivityTracker.Services.Sinks;
using ActivityTracker.Views.Dev;

namespace ActivityTracker.Startup;

// 服务注册集中在这里。
// 以前这些对象全是在 MainWindow 构造函数里 new 出来的，
// 现在交给容器管理，MainWindow 只负责显示。
// 主要作用是告诉Hosting容器如何创建这些服务对象，以及它们的生命周期（单例、瞬态、作用域等）。
internal static class ServiceRegistration
{
    public static IServiceCollection AddActivityTracker(
        this IServiceCollection services,
        string wakePipeName)
    {
        // ==============================
        // 数据层：三个仓储共用同一个数据库文件
        // ==============================

        //AddSingleton表示只创建一个，第一次创建，之后复用
        services.AddSingleton(sp =>
            new SqliteConnectionFactory(
                AppPaths.DatabasePath,
                sp.GetRequiredService<ILogger<SqliteConnectionFactory>>()));
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<ActivityRepository>();
        services.AddSingleton<DailyRepository>();
        services.AddSingleton<TodoRepository>();

        // ==============================
        // 业务服务
        // ==============================

        services.AddSingleton<AppIdentityResolver>();
        services.AddSingleton<ForegroundWindowTracker>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<SessionSegmentManager>();
        services.AddSingleton<TrackingStatusService>();
        services.AddSingleton<StatisticsService>();//构造函数中有依赖注入的参数，容器会自动解析
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<UiActivityNotifier>();
        services.AddSingleton<IPresencePublisher, LoggingPresencePublisher>();
        services.AddSingleton<SessionTracker>();

        // ==============================
        // 托盘
        // ==============================

        services.AddSingleton<TrayService>();

        // ==============================
        // 后台任务
        //
        // 先注册成具体类型，再以 IHostedService 暴露同一个实例：
        // Host 启动时会把所有 IHostedService 跑一遍，
        // 同时 App 还能拿到具体类型去订阅它们的事件。
        // ==============================

        services.AddSingleton<PowerEventListener>();
        services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<PowerEventListener>());

        // 下面五项的相对注册顺序也是关停契约。Host 逆序停止，因此先停
        // tracker，再停 UI/Presence，最后由 DB sink 排空总线、重试并 checkpoint。
        services.AddSingleton<ActivityChangeBus>();
        services.AddSingleton<IActivityChangeBus>(
            sp => sp.GetRequiredService<ActivityChangeBus>());

        services.AddSingleton<ActivityDatabaseSink>();
        services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<ActivityDatabaseSink>());

        services.AddSingleton<ActivityPresenceSink>();
        services.AddSingleton<IPresenceStateProvider>(
            sp => sp.GetRequiredService<ActivityPresenceSink>());
        services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<ActivityPresenceSink>());

        services.AddSingleton<ActivityUiSink>();
        services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<ActivityUiSink>());

        services.AddSingleton<TrackingHostedService>();
        services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<TrackingHostedService>());

        services.AddSingleton(sp =>
            new WakeListenerService(
                wakePipeName,
                sp.GetRequiredService<ILogger<WakeListenerService>>()));
        services.AddSingleton<IHostedService>(
            sp => sp.GetRequiredService<WakeListenerService>());

        // ==============================
        // 主窗口
        //
        // 按单例创建：MainWindow 和 StatisticsView 都订阅了
        // SessionTracker.SessionChanged 且从不退订，
        // 只要窗口只建一次，这些订阅就不会泄漏。
        // 窗口重建的问题留到页面导航那一阶段再处理。
        // ==============================

        services.AddSingleton<MainWindow>();
        services.AddSingleton<StyleGalleryWindow>();

        return services;
    }
}
