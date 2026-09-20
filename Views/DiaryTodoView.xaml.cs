using System;
using System.Windows;
using System.Windows.Controls;

using ActivityTracker.Data;

namespace ActivityTracker.Views;

public partial class DiaryTodoView : System.Windows.Controls.UserControl
{
    private readonly DailyRepository _dailyRepo;
    private readonly TodoRepository _todoRepo;

    public DiaryTodoView(
        DailyRepository dailyRepo,
        TodoRepository todoRepo)
    {
        InitializeComponent();

        _dailyRepo = dailyRepo;
        _todoRepo = todoRepo;

        // 默认选中今天
        DatePicker.SelectedDate = DateTime.Today;
    }


    // ==============================
    // 日期切换时重新加载数据
    // ==============================

    private void DatePicker_SelectedDateChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (DatePicker.SelectedDate is null)
            return;

        LoadDiary(DatePicker.SelectedDate.Value);
        LoadTodos(DatePicker.SelectedDate.Value);
    }


    // ==============================
    // 日记相关
    // ==============================

    // 加载指定日期的日记内容
    private void LoadDiary(DateTime date)
    {
        var entry = _dailyRepo.GetByDate(date);

        DiaryTitleBox.Text = entry?.Title ?? "";
        DiaryTextBox.Text = entry?.Content ?? "";
        DiaryTagsBox.Text = entry?.Tags ?? "";

        // 索引 0 表示未记录
        MoodBox.SelectedIndex = entry?.Mood ?? 0;
    }

    // 保存日记：始终基于当前选中日期来判断是新建还是更新
    private void SaveDiary_Click(
        object sender, RoutedEventArgs e)
    {
        var date = DatePicker.SelectedDate;

        if (date is null)
            return;

        var entry = new DailyEntry
        {
            Date = date.Value.Date,
            Title = DiaryTitleBox.Text?.Trim() ?? "",
            Content = DiaryTextBox.Text?.Trim() ?? "",
            Tags = DiaryTagsBox.Text?.Trim() ?? "",
            Mood = MoodBox.SelectedIndex > 0
                ? MoodBox.SelectedIndex
                : null
        };

        // 先查一下当前选中日期是否已有日记
        var existing = _dailyRepo.GetByDate(date.Value);

        if (existing is not null)
        {
            if (IsBlank(entry))
            {
                // 内容全清空则删掉这一天的日记
                _dailyRepo.Delete(existing.Id);
            }
            else
            {
                entry.Id = existing.Id;
                _dailyRepo.Update(entry);
            }
        }
        else
        {
            if (IsBlank(entry))
                return;

            _dailyRepo.Insert(entry);
        }
    }

    // 标题、正文、标签、心情全空才算没内容
    private static bool IsBlank(DailyEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.Title)
            && string.IsNullOrWhiteSpace(entry.Content)
            && string.IsNullOrWhiteSpace(entry.Tags)
            && entry.Mood is null;
    }


    // ==============================
    // 待办事项相关
    // ==============================

    // 加载指定日期的待办事项
    private void LoadTodos(DateTime date)
    {
        var todos = _todoRepo.GetByDate(date);
        TodoList.ItemsSource = todos;

        // 没有待办时显示提示
        NoTodoHint.Visibility = todos.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // 点击"添加待办"按钮，弹出输入窗口
    private void AddTodo_Click(
        object sender, RoutedEventArgs e)
    {
        var date = DatePicker.SelectedDate?.Date;

        if (date is null)
            return;

        var dialog = new AddTodoDialog(date.Value)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true
            && dialog.Result is not null)
        {
            _todoRepo.Insert(dialog.Result);

            LoadTodos(date.Value);
        }
    }

    // 勾选/取消勾选待办
    private void TodoCheckBox_Click(
        object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox checkbox
            && checkbox.DataContext is TodoItem item)
        {
            var date = DatePicker.SelectedDate?.Date;

            if (date is null)
                return;

            if (item.IsCompleted)
                _todoRepo.Complete(item.Id);
            else
                _todoRepo.Uncomplete(item.Id);

            LoadTodos(date.Value);
        }
    }

    // 删除待办
    private void DeleteTodo_Click(
        object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button
            && button.Tag is long id)
        {
            var result = System.Windows.MessageBox.Show(
                "确定要删除这条待办吗？",
                "确认删除",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                _todoRepo.Delete(id);

                var date =
                    DatePicker.SelectedDate?.Date;

                if (date is not null)
                    LoadTodos(date.Value);
            }
        }
    }
}
