using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// StreamingAssets/&lt;imageFolder&gt;/ 안의 이미지 파일을 런타임에 읽어들인다.
/// 기본 규칙은 "image_&lt;마커ID&gt;.png" 이고, 폴더에 파일을 넣는 것만으로 페이지가 늘어난다.
///
/// 빌드에 포함되지 않는 경로이므로 현장에서 png 를 넣거나 교체한 뒤
/// 편집모드에서 다시 읽기(F5)를 누르면 재빌드 없이 반영된다.
/// </summary>
public class ArUcoImageLibrary
{
    static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

    readonly Dictionary<int, Texture2D> _cache = new Dictionary<int, Texture2D>();
    string _folderPath;
    string _prefix = "image_";

    public string FolderPath => _folderPath;

    public void SetFolder(string folderName, string prefix)
    {
        string next = Path.Combine(Application.streamingAssetsPath,
            string.IsNullOrEmpty(folderName) ? "Images" : folderName);

        _prefix = prefix ?? "";

        if (next == _folderPath) return;

        _folderPath = next;
        Clear();

        if (!Directory.Exists(_folderPath))
        {
            Directory.CreateDirectory(_folderPath);
            Debug.LogWarning($"[ArUco] 이미지 폴더가 없어 새로 만들었습니다: {_folderPath}");
        }
    }

    /// <summary>
    /// 폴더에 들어 있는 파일 이름에서 마커 ID 를 뽑아낸다.
    /// 페이지를 추가할 때 json 을 미리 손보지 않고 png 만 넣어도 되도록 하기 위한 것.
    /// </summary>
    public List<int> ScanMarkerIds()
    {
        var ids = new List<int>();
        if (string.IsNullOrEmpty(_folderPath) || !Directory.Exists(_folderPath)) return ids;

        foreach (string path in Directory.GetFiles(_folderPath))
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (System.Array.IndexOf(Extensions, extension) < 0) continue;

            string name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(_prefix))
            {
                if (!name.StartsWith(_prefix, System.StringComparison.OrdinalIgnoreCase)) continue;
                name = name.Substring(_prefix.Length);
            }

            if (int.TryParse(name, out int id) && !ids.Contains(id))
                ids.Add(id);
        }

        ids.Sort();
        return ids;
    }

    /// <summary>마커 하나에 대응하는 이미지. 없으면 null.</summary>
    public Texture2D Get(ArUcoMarkerConfig config)
    {
        if (config == null) return null;

        if (_cache.TryGetValue(config.id, out var cached))
            return cached;

        Texture2D loaded = Load(config);
        // 실패한 경우에도 null 을 캐시해 매 프레임 디스크를 두드리지 않게 한다.
        _cache[config.id] = loaded;
        return loaded;
    }

    Texture2D Load(ArUcoMarkerConfig config)
    {
        if (string.IsNullOrEmpty(_folderPath)) return null;

        string path = null;

        if (!string.IsNullOrEmpty(config.image))
        {
            string candidate = Path.Combine(_folderPath, config.image);
            if (File.Exists(candidate)) path = candidate;
        }
        else
        {
            foreach (string extension in Extensions)
            {
                string candidate = Path.Combine(_folderPath, _prefix + config.id + extension);
                if (File.Exists(candidate)) { path = candidate; break; }
            }
        }

        if (path == null)
        {
            string expected = string.IsNullOrEmpty(config.image) ? _prefix + config.id + ".png" : config.image;
            Debug.LogWarning($"[ArUco] {config.id}번 마커의 이미지를 찾지 못했습니다. ({_folderPath} 안에 {expected} 필요)");
            return null;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(File.ReadAllBytes(path)))
        {
            Object.Destroy(texture);
            Debug.LogError($"[ArUco] 이미지를 해석하지 못했습니다: {path}");
            return null;
        }

        texture.wrapMode = TextureWrapMode.Clamp;
        texture.name = Path.GetFileName(path);
        return texture;
    }

    /// <summary>현장에서 png 를 갈아끼운 뒤 다시 읽게 한다.</summary>
    public void Clear()
    {
        foreach (var texture in _cache.Values)
        {
            if (texture != null) Object.Destroy(texture);
        }
        _cache.Clear();
    }
}
