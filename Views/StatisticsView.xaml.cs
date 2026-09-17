using ActivityTracker.Services;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ActivityTracker.Views;

public partial class StatisticsView : System.Windows.Controls.UserControl
{
    private readonly StatisticsService _statisticsService;
    private readonly SessionTracker _sessionTracker;

    private DateTime _rangeStart;
    private DateTime _rangeEnd;

    public StatisticsView(
        StatisticsService statisticsService,
        SessionTracker sessionTracker)
    {
        InitializeComponent();

        _statisticsService =
            statisticsService;

        _sessionTracker =
            sessionTracker;

        Loaded += StatisticsView_Loaded;

        // Session 变化以后刷新统计
        _sessionTracker.SessionChanged +=
            OnSessionChanged;
    }


    private void StatisticsView_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        ShowToday();
    }


    // SessionTracker 可能从后台线程触发事件，
    // 所以需要回到 UI 线程
    private void OnSessionChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            RefreshStatistics();
        });
    }


    // ==============================
    // 今日
    // ==============================

    private void Today_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowToday();
    }

    private void ShowToday()
    {
        var today =
            DateTime.Today;

        SetRange(
            today,
            today.AddDays(1));
    }


    // ==============================
    // 近7天
    // ==============================

    private void SevenDays_Click(
        object sender,
        RoutedEventArgs e)
    {
        var today =
            DateTime.Today;

        SetRange(
            today.AddDays(-6),
            today.AddDays(1));
    }


    // ==============================
    // 近30天
    // ==============================

    private void ThirtyDays_Click(
        object sender,
        RoutedEventArgs e)
    {
        var today =
            DateTime.Today;

        SetRange(
            today.AddDays(-29),
            today.AddDays(1));
    }


    // ==============================
    // 本月
    // ==============================

    private void ThisMonth_Click(
        object sender,
        RoutedEventArgs e)
    {
        var today =
            DateTime.Today;

        var firstDay =
            new DateTime(
                today.Year,
                today.Month,
                1);

        SetRange(
            firstDay,
            today.AddDays(1));
    }


    // ==============================
    // 今年
    // ==============================

    private void ThisYear_Click(
        object sender,
        RoutedEventArgs e)
    {
        var today =
            DateTime.Today;

        var firstDay =
            new DateTime(
                today.Year,
                1,
                1);

        SetRange(
            firstDay,
            today.AddDays(1));
    }


    // ==============================
    // 自定义日期
    // ==============================

    private void CustomRange_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (StartDatePicker.SelectedDate is null ||
            EndDatePicker.SelectedDate is null)
        {
            System.Windows.MessageBox.Show(
                "请选择开始日期和结束日期。");

            return;
        }

        var start =
            StartDatePicker
                .SelectedDate.Value.Date;

        // 结束日期当天也包含在内
        var end =
            EndDatePicker
                .SelectedDate.Value.Date
                .AddDays(1);

        if (end <= start)
        {
            System.Windows.MessageBox.Show(
                "结束日期不能早于开始日期。");

            return;
        }

        SetRange(
            start,
            end);
    }


    // ==============================
    // 手动刷新
    // ==============================

    private void Refresh_Click(
        object sender,
        RoutedEventArgs e)
    {
        RefreshStatistics();
    }


    // ==============================
    // 设置统计范围
    // ==============================

    private void SetRange(
        DateTime start,
        DateTime end)
    {
        _rangeStart = start;
        _rangeEnd = end;

        StartDatePicker.SelectedDate =
            start.Date;

        EndDatePicker.SelectedDate =
            end.AddDays(-1).Date;

        RefreshStatistics();
    }


    // ==============================
    // 真正执行统计
    // ==============================

    private void RefreshStatistics()
    {
        if (_rangeEnd <= _rangeStart)
            return;

        // 获取当前尚未写入 SQLite 的 Session
        var current =
            _sessionTracker.GetCurrentSnapshot();

        var result =
            _statisticsService.GetAppUsage(
                _rangeStart,
                _rangeEnd,
                current);

        StatisticsGrid.ItemsSource =
            result;

        var totalSeconds =
            result.Sum(
                x => x.TotalSeconds);

        StatisticsSummary.Text =
            $"活跃时间 {Format(totalSeconds)}    " +
            $"应用 {result.Count} 个";
    }


    private static string Format(
        int seconds)
    {
        var span =
            TimeSpan.FromSeconds(seconds);

        return
            $"{(int)span.TotalHours:D2}:" +
            $"{span.Minutes:D2}:" +
            $"{span.Seconds:D2}";
    }
}