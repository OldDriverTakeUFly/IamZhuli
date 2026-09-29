using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace IamZhuli.Unity.EditorTools
{
    /// <summary>
    /// 一键构建 Linux 单机版:校验场景/DLL → 打包可执行文件到 unity/build/IamZhuli。
    /// 用法:菜单 我是主力 → 构建Linux单机版;或批处理
    /// Unity -batchmode -quit -projectPath ... -executeMethod IamZhuli.Unity.EditorTools.LinuxBuild.Build
    /// </summary>
    public static class LinuxBuild
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string OutPath = "../build/IamZhuli";   // 相对工程根 → unity/build/IamZhuli

        [MenuItem("我是主力/构建Linux单机版")]
        public static void Build()
        {
            // 1. 场景存在性(没有则现场搭)
            if (!File.Exists(ScenePath))
            {
                Debug.Log("未找到场景,先执行一键搭建...");
                SceneBuilder.Build(interactive: false);
            }

            // 2. 切 Linux(已在该平台则跳过)
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneLinux64)
            {
                Debug.Log("切换到 Linux x86_64 平台...");
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64);
            }

            // 3. 打包(Development:可调试;发布版去掉该 flag 减体积)
            Directory.CreateDirectory("../build");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                target = BuildTarget.StandaloneLinux64,
                locationPathName = OutPath,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.Development,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var ok = report.summary.result == BuildResult.Succeeded;
            Debug.Log($"[LinuxBuild] result={report.summary.result} size={report.summary.totalSize}B " +
                      $"errors={report.summary.totalErrors} path={Path.GetFullPath(OutPath)}");

            if (Application.isBatchMode)
                EditorApplication.Exit(ok ? 0 : 1);
            else if (ok)
                EditorUtility.DisplayDialog("我是主力", "Linux 单机版构建完成:\n" + Path.GetFullPath(OutPath), "好");
        }
    }
}
