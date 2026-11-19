using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 一个即时桥：正在运行的 Editor 每隔一会儿看一眼工程的 .bridge/in，有命令就执行，
/// 把结果写进 .bridge/out。
///
/// 存在的理由是授权。这台机器上的 Unity 在 batchmode 下拿不到 headless entitlement，
/// 2019 与 2022 两个版本都过不去，所以「无人值守地驱动 Editor」这件事走不通。
/// 但 GUI 授权是好的——打开工程、编译、按 Play 都没有问题。于是换一条路：
/// 不把 Editor 当外部程序去启动，而是让已经在跑的这个自己成为一个可被驱动的对象。
///
/// 为什么用文件而不是 socket：Editor 里开网络要自己起线程、自己管生命周期、自己处理
/// 域重载之后的残留，而读一次目录只要几微秒。0.25 秒的轮询比一次眨眼还短。
///
/// 目录放在工程根而不是 Assets 下面，Unity 就不会去导入它——命令文件不该触发资源刷新，
/// 否则每发一条命令编辑器都要重新编译一遍。
///
/// 这个文件在 Assets/Editor 下，所以它只存在于编辑器里，不会进任何构建产物。
/// </summary>
[InitializeOnLoad]
public static class ClarryBridge
{
    const string InDir = ".bridge/in";
    const string OutDir = ".bridge/out";
    const double PollSeconds = 0.25;

    static double _next;
    static readonly List<LogEntry> _logs = new List<LogEntry>();

    /// <summary>
    /// 工程根，从 Application.dataPath 反推，而不是信 Directory.GetCurrentDirectory()。
    /// 工作目录在编辑器里「通常」等于工程根，而通常这个词在这里不够用：一旦它变了，
    /// 桥就安静地读不到任何命令，而且不报错——那种失败最难查。
    /// </summary>
    static string Root
    {
        get { return Directory.GetParent(Application.dataPath).FullName; }
    }

    static string InPath
    {
        get { return Path.Combine(Root, ".bridge", "in"); }
    }

    static string OutPath
    {
        get { return Path.Combine(Root, ".bridge", "out"); }
    }

    [Serializable]
    public class LogEntry
    {
        public string type;
        public string message;
        public string time;
    }

    [Serializable]
    public class Command
    {
        public string command;
        public string arg;
    }

    [Serializable]
    public class Reply
    {
        public bool ok;
        public string command;
        public string message;
        public bool isPlaying;
        public bool isCompiling;
        public string projectPath;
        public string unityVersion;
        public LogEntry[] logs;
    }

    static ClarryBridge()
    {
        // 只留会让人停下来看的那几种：普通 Log 一排一排的，抓了也没人读。
        Application.logMessageReceived += OnLog;
        EditorApplication.update += Tick;
        Debug.Log("[bridge] 已挂上，等命令在 " + InPath);
    }

    static void OnLog(string message, string stack, LogType type)
    {
        if (type == LogType.Log) return;

        _logs.Add(new LogEntry
        {
            type = type.ToString(),
            message = message,
            time = DateTime.Now.ToString("HH:mm:ss")
        });

        if (_logs.Count > 200) _logs.RemoveRange(0, _logs.Count - 200);
    }

    static void Tick()
    {
        var now = EditorApplication.timeSinceStartup;
        if (now < _next) return;
        _next = now + PollSeconds;

        try
        {
            Drain();
        }
        catch (Exception e)
        {
            Debug.LogError("[bridge] " + e.GetType().Name + ": " + e.Message);
        }
    }

    static void Drain()
    {
        var inPath = InPath;
        if (!Directory.Exists(inPath)) return;

        var files = Directory.GetFiles(inPath, "*.cmd");
        if (files.Length == 0) return;

        Array.Sort(files, StringComparer.Ordinal);

        var outPath = OutPath;
        if (!Directory.Exists(outPath)) Directory.CreateDirectory(outPath);

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);

            Command cmd = null;
            string readError = null;
            try
            {
                cmd = JsonUtility.FromJson<Command>(File.ReadAllText(file));
            }
            catch (Exception e)
            {
                readError = e.Message;
            }

            // Run 里出的任何异常都要变成一条回执，而不是让整批命令卡在这里：
            // 卡住的那条命令文件不会被删，于是下一次 Tick 再抛一次，桥就再也不动了。
            Reply reply;
            if (readError != null)
            {
                reply = new Reply { ok = false, command = "(unreadable)", message = readError };
            }
            else
            {
                try
                {
                    reply = Run(cmd);
                }
                catch (Exception e)
                {
                    reply = new Reply
                    {
                        ok = false,
                        command = cmd != null ? cmd.command : "(null)",
                        message = "处理这条命令时抛了：" + e.GetType().Name + ": " + e.Message
                    };
                }
            }

            reply.isPlaying = EditorApplication.isPlaying;
            reply.isCompiling = EditorApplication.isCompiling;
            reply.projectPath = Root;
            reply.unityVersion = Application.unityVersion;

            File.WriteAllText(Path.Combine(outPath, name + ".json"), JsonUtility.ToJson(reply, true));
            File.Delete(file);
        }
    }

    static Reply Run(Command cmd)
    {
        var reply = new Reply { command = cmd != null ? cmd.command : "(null)" };

        if (cmd == null)
        {
            reply.ok = false;
            reply.message = "命令文件里没有内容";
            return reply;
        }

        switch (cmd.command)
        {
            case "status":
                reply.ok = true;
                reply.message = "状态";
                break;

            case "play":
                if (EditorApplication.isPlaying)
                {
                    reply.ok = true;
                    reply.message = "本来就在跑";
                }
                else if (EditorApplication.isCompiling)
                {
                    reply.ok = false;
                    reply.message = "正在编译，等一会儿再按";
                }
                else
                {
                    EditorApplication.EnterPlaymode();
                    reply.ok = true;
                    reply.message = "已请求进入播放模式";
                }
                break;

            case "stop":
                if (!EditorApplication.isPlaying)
                {
                    reply.ok = true;
                    reply.message = "本来就没在跑";
                }
                else
                {
                    EditorApplication.ExitPlaymode();
                    reply.ok = true;
                    reply.message = "已请求退出播放模式";
                }
                break;

            case "log":
                reply.ok = true;
                reply.message = _logs.Count + " 条";
                reply.logs = _logs.ToArray();
                break;

            case "clearlog":
                _logs.Clear();
                reply.ok = true;
                reply.message = "已清空";
                break;

            case "refresh":
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                reply.ok = true;
                reply.message = "已请求刷新资源";
                break;

            case "shot":
                reply.ok = false;
                reply.message = "截图不走这条桥，见 TryShot 的注释";
                break;

            case "game":
                {
                    var gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
                    if (gameViewType == null)
                    {
                        reply.ok = false;
                        reply.message = "找不到 UnityEditor.GameView 这个类型";
                        break;
                    }

                    // Game 与 Scene 停靠在同一个位置，同一时刻只有激活的那个有窗口句柄。
                    // 所以想从外面抓 Game，就得先让 Unity 把它切到前面来。
                    var win = EditorWindow.GetWindow(gameViewType, false, null, true);
                    if (win == null)
                    {
                        reply.ok = false;
                        reply.message = "拿不到 Game 窗口";
                        break;
                    }

                    win.Focus();
                    win.Repaint();
                    reply.ok = true;
                    reply.message = "已切到 Game 视图";
                    break;
                }

            default:
                reply.ok = false;
                reply.message = "不认识这条命令：" + cmd.command;
                break;
        }

        return reply;
    }

    /// <summary>
    /// 截图这件事我试过两版，都退回来了，理由值得写下来免得下次重走。
    ///
    /// 第一版用 ScreenCapture.CaptureScreenshot：它排得进去、报成功，文件从来不出现——
    /// 那个 API 在编辑器里的行为不可靠。
    /// 第二版用 ScreenCapture.CaptureScreenshotAsTexture 自己编码：这个类属于
    /// com.unity.modules.screencapture，而运行时程序集只引用 manifest 里声明的模块；
    /// 把模块加进 manifest 之后仍然编不过，因为包要重新解析，Editor 不会因为一次 refresh
    /// 就重解析。把它放回 Assets/Editor 也不行——Editor 程序集里的 MonoBehaviour 加不到
    /// GameObject 上，Unity 会拒绝。
    ///
    /// 所以画面交给看屏幕的人。桥保留 play / stop / log / refresh 四件事，加上 log 里
    /// 那些 warning 与 error，足够判断一个客户端有没有连上、跑得对不对。
    /// </summary>
    static bool TryShot(string path, out string why)
    {
        why = "截图不走这条桥" + (string.IsNullOrEmpty(path) ? "" : "（原本要给 " + path + "）");
        return false;
    }
}
