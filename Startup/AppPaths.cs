using System;
using System.IO;

namespace ActivityTracker.Startup;

// 程序运行时用到的路径集中在这里。
// 以前这段路径是在 MainWindow 构造函数里拼的，
// 现在挪出来，以后加配置文件和日志目录也从这里取。
internal static class AppPaths//静态工具类
{
    // %LOCALAPPDATA%\ActivityTracker
    private static readonly string Root =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),//得到形如C:\Users\你的用户名\AppData\Local
            "ActivityTracker");

    // SQLite 数据库文件
    public static string DatabasePath =>
        Path.Combine(Root, "activity.db");
    /*
      public static string DatabasePath
    {
        get
        {
            return Path.Combine(Root, "activity.db");
        }
    }等价，而且这是为了防止改变DatabasePath的值，使用只读属性，也可以使用readonly字段来实现只读，readonly字段只能在声明时或构造函数中赋值，之后不能修改。
     */

    // 三个 Repository 都要往这个目录写，
    // 在创建任何 Repository 之前先确保目录存在
    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(Root);//如果目录不存在就创建目录，如果存在就什么都不做
    }
}
