using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// ArUcoPages 폴더를 .unitypackage 로 내보낸다.
/// 다른 프로젝트에서는 이 파일을 Assets 에 드래그하거나
/// Assets > Import Package > Custom Package 로 가져오면 된다.
/// </summary>
public static class ArUcoPackageExporter
{
    const string PackageRoot = "Assets/ArUcoPages";

    [MenuItem("Tools/ArUco 패키지 내보내기")]
    static void Export()
    {
        if (!AssetDatabase.IsValidFolder(PackageRoot))
        {
            EditorUtility.DisplayDialog("ArUco", $"폴더를 찾지 못했습니다: {PackageRoot}", "확인");
            return;
        }

        string path = EditorUtility.SaveFilePanel(
            "ArUcoPages 패키지 저장",
            Path.GetDirectoryName(Application.dataPath),
            "ArUcoPages.unitypackage",
            "unitypackage");

        if (string.IsNullOrEmpty(path)) return;

        // Recurse 만 쓴다. IncludeDependencies 를 켜면 OpenCVForUnity 전체(수 GB)가 딸려 들어간다.
        AssetDatabase.ExportPackage(PackageRoot, path, ExportPackageOptions.Recurse);

        Debug.Log($"[ArUco] 패키지를 내보냈습니다: {path}");
        EditorUtility.RevealInFinder(path);
    }
}
