/*
MIT License

Copyright (c) 2021 Chillu

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

// 来源:https://gist.github.com/Chillu1/4c209308dc81104776718b1735c639f7
// 原作者:Chillu。按 MIT 协议收录进本项目,保留原版权声明。
// 本项目内的改动:
//   · 补了 gameViewType 为 null 时的判空(原版会在静态构造里抛异常);
//   · 补了中文说明注释。

#if UNITY_EDITOR

using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 全屏 Game 视图:一进 Play 模式就弹出一个铺满显示器的「Game」窗口,退出 Play 自动关掉。
/// 做 demo 录屏用 —— 编辑器里的 Game 视图带着分辨率下拉框、Stats、Gizmos 那一堆白边,
/// 录进来很乱;这个窗口把工具栏也隐藏了(showToolbar = false),画面就是纯游戏画面。
///
/// 用法:
///   · 什么都不用做,点 Play 就自动全屏;
///   · 想手动开关:菜单 Window → General → Game (Fullscreen),快捷键 Ctrl+Shift+Alt+2。
///
/// 想关掉自动全屏:把下面的 <see cref="fullscreen"/> 改成 false,就只剩手动菜单。
///
/// 【注意】
///   1. 它开的是一个**独立的新 Game 视图窗口**,覆盖在编辑器上面;退出 Play 会自己关。
///      万一没关掉(比如脚本重载打断),Alt+F4 关它 —— 它就是个普通窗口。
///   2. 全屏是开在 Unity 认为的"主显示器"上。双显示器时 Unity 可能还会另一个 Game 视图,
///      那个窗口请把它设成不渲染的 Display(见作者原话),否则白多一份渲染开销。
///   3. 录制时把 Game 视图的分辨率设成和录屏区域一致,免得画面被拉伸。
///      本项目主菜单/战斗 UI 是按 1920×1080 设计的,建议就用它。
/// </summary>
[InitializeOnLoad]
public static class FullscreenGameView
{
    private static readonly Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");

    // gameViewType 拿不到时(Unity 改了内部类名)就是 null,GetProperty 会抛异常 —— 先判空。
    // 这里用 ?. 而不是让 static 构造函数炸掉:炸掉的话整个编辑器都会起不来。
    private static readonly PropertyInfo showToolbarProperty =
        gameViewType?.GetProperty("showToolbar", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly object falseObject = false; // 只装箱一次。作者原话:「这是原则问题」

    private static EditorWindow _instance;

    /// <summary>进 Play 自动全屏的总开关。改成 false 就只保留手动菜单</summary>
    private static readonly bool fullscreen = true;

    static FullscreenGameView()
    {
        EditorApplication.playModeStateChanged -= ToggleFullScreen;
        if (!fullscreen)
            return;
        EditorApplication.playModeStateChanged += ToggleFullScreen;
    }

    [MenuItem("Window/General/Game (Fullscreen) %#&2", priority = 2)]
    public static void Toggle()
    {
        ToggleFullScreen(PlayModeStateChange.EnteredPlayMode);
    }

    public static void ToggleFullScreen(PlayModeStateChange playModeStateChange)
    {
        // 回到编辑模式 / 正离开编辑模式 = 该关窗了
        if (playModeStateChange == PlayModeStateChange.EnteredEditMode ||
            playModeStateChange == PlayModeStateChange.ExitingEditMode)
        {
            CloseGameWindow();
            return;
        }

        if (gameViewType == null)
        {
            Debug.LogError("[全屏Game视图] 找不到 UnityEditor.GameView 类型 —— " +
                           "多半是 Unity 版本改了内部类名,这个插件需要跟着改。");
            return;
        }

        if (showToolbarProperty == null)
        {
            Debug.LogWarning("[全屏Game视图] 找不到 GameView.showToolbar —— " +
                             "窗口还是会开,但顶上的工具栏藏不掉。");
        }

        switch (playModeStateChange)
        {
            case PlayModeStateChange.ExitingPlayMode:
                return;
            case PlayModeStateChange.EnteredPlayMode: // 手动菜单也走这里,所以兼作"再按一次就关"
                if (CloseGameWindow())
                    return;
                break;
        }

        _instance = (EditorWindow)ScriptableObject.CreateInstance(gameViewType);

        showToolbarProperty?.SetValue(_instance, falseObject);

        var desktopResolution = new Vector2(Screen.currentResolution.width, Screen.currentResolution.height);
        var fullscreenRect = new Rect(Vector2.zero, desktopResolution);
        _instance.ShowPopup();
        _instance.position = fullscreenRect;
        _instance.Focus();
    }

    private static bool CloseGameWindow()
    {
        if (_instance != null)
        {
            _instance.Close();
            _instance = null;
            return true;
        }

        return false;
    }
}
#endif
