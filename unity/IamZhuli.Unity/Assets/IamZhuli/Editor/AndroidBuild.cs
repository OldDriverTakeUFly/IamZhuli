using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace IamZhuli.Unity.EditorTools
{
    /// <summary>
    /// 一键构建安卓 APK:切 Android 平台 → 校验场景/DLL → 打包到 unity/build/IamZhuli.apk。
    /// 前置:编辑器装了 Android Build Support 模块(Unity Hub 安装时勾选)。
    /// 用法:菜单 我是主力 → 构建安卓APK。
    /// </summary>
    public static class AndroidBuild
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string ApkPath = "../build/IamZhuli.apk";   // 相对工程根

        [MenuItem("我是主力/构建安卓APK")]
        public static void Build()
        {
            // 1. 场景存在性(没有则现场搭)
            if (!File.Exists(ScenePath))
            {
                Debug.Log("未找到场景,先执行一键搭建...");
                SceneBuilder.Build(interactive: false);
            }

            // 2. 切 Android(已在该平台则跳过)
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                Debug.Log("切换到 Android 平台(首次较慢,需导入模块)...");
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            }

            // 3. 打包(Development:可调试,体积大;发布版去掉该 flag)
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                target = BuildTarget.Android,
                locationPathName = ApkPath,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.Development,
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result == BuildResult.Succeeded)
            {
                var path = Path.GetFullPath(ApkPath);
                EditorUtility.DisplayDialog("我是主力",
                    $"APK 构建成功:\n{path}\n大小 {report.summary.totalSize / 1024 / 1024} MB\n\nadb install 或直接传到手机安装。", "好");
                Debug.Log($"APK: {path} ({report.summary.totalSize / 1024 / 1024} MB)");
            }
            else
            {
                EditorUtility.DisplayDialog("我是主力", $"构建失败: {report.summary.result}\n详见 Console 日志。", "知道了");
                Debug.LogError($"Android 构建失败: {report.summary.result}, 错误 {report.summary.totalErrors}");
            }
        }
    }
}
