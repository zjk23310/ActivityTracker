using System;
using System.Windows;

using ActivityTracker.Data;

namespace ActivityTracker.Views;

public partial class AddTodoDialog : Window
{
    // 确定后填写的结果，取消时保持 null
    public TodoItem? Result { get; private set; }

    public AddTodoDialog(DateTime dueDate)
    {
        InitializeComponent();

        DueDatePicker.SelectedDate = dueDate;

        // 自动聚焦到标题输入框
        Loaded += (_, _) => TitleBox.Focus();
    }

    // 勾上「具体到时刻」才能填时间
    private void ExactTimeCheck_Changed(
        object sender, RoutedEventArgs e)
    {
        TimeBox.IsEnabled = ExactTimeCheck.IsChecked == true;

        if (TimeBox.IsEnabled)
            TimeBox.Focus();
    }

    private void Ok_Click(
        object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim();

        if (string.IsNullOrEmpty(title))
        {
            System.Windows.MessageBox.Show(
                "请输入待办标题。",
                "提示",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (DueDatePicker.SelectedDate is null)
        {
            System.Windows.MessageBox.Show(
                "请选择截止日期。",
                "提示",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        // 时刻是可选的，勾了就必须填对
        string? dueTime = null;

        if (ExactTimeCheck.IsChecked == true)
        {
            var raw = TimeBox.Text?.Trim();

            if (!TimeOnly.TryParse(raw, out var parsed))
            {
                System.Windows.MessageBox.Show(
                    "时刻格式不对，请按 24 小时制填写，例如 14:30。",
                    "提示",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            dueTime = parsed.ToString("HH:mm");
        }

        Result = new TodoItem
        {
            Title = title,
            Note = NoteBox.Text?.Trim() ?? "",
            Priority = PriorityBox.SelectedIndex,
            Category = CategoryBox.Text?.Trim() ?? "",
            DueDate = DueDatePicker.SelectedDate.Value.Date,
            DueTime = dueTime
        };

        DialogResult = true;
    }

    private void Cancel_Click(
        object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
