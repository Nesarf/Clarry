using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace ClarryStage
{
    /// <summary>
    /// Clarry 的 HTTP 面在这边的全部形状。
    ///
    /// 这些类是从服务端返回的 JSON 抄下来的，不是共享的模型。Clarry.Core 停在 C# 13，
    /// 用了 required 成员与 file 作用域命名空间，Unity 2022.3 的 C# 9 编译不了；
    /// 为了抄这几个字段把两边绑在同一个编译器版本上，代价比抄一次大。
    ///
    /// JsonUtility 不认顶层数组，而 /api/cast 与 /api/history 返回的就是数组，
    /// 所以每个数组都套一层 {"items": ...} 再交过去。
    /// </summary>
    public static class ClarryApi
    {
        /// <summary>Where the theatre is. Change it here, nowhere else.</summary>
        public static string BaseUrl = "http://127.0.0.1:3090";

        /// <summary>
        /// 六维与那两样状态里，JsonUtility 处理得了的部分。
        ///
        /// 响应里的 bond 与 names 是对象，JsonUtility 不支持字典 —— 抄成 Dictionary 会让整个
        /// 成员的反序列化失败，而失败的样子是「所有字段都是默认值」，不是一条错误。所以它们
        /// 不在这份 DTO 里。ember 在服务端是布尔（有没有余火），早先这里抄成了 int，那种错
        /// 也一样安静：类型对不上时 JsonUtility 跳过那个字段，读出来永远是 0。
        /// scripts/unity-api-check.ps1 会把这两类漂移都摆出来。
        /// </summary>
        [Serializable]
        public class Attributes
        {
            public int poise;
            public int insight;
            public int renown;
            public int strain;
            public int reserve;
            public bool ember;
        }

        [Serializable]
        public class CastMember
        {
            public string id;
            public string name;
            public string role;
            public string summary;
            public string expression;
            public string expressionText;
            public string shot;
            public string shotText;
            public string face;
            public string[] faces;
            public bool driven;
            public Attributes a;
        }

        [Serializable]
        public class Turn
        {
            public string actorId;
            public string kind;
            public string text;
            public string addressedTo;
        }

        [Serializable] public class CastList { public CastMember[] items; }
        [Serializable] public class TurnList { public Turn[] items; }

        public static IEnumerator GetCast(Action<CastMember[]> done)
        {
            return Get("/api/cast", json =>
            {
                var list = JsonUtility.FromJson<CastList>("{\"items\":" + json + "}");
                done?.Invoke(list != null && list.items != null ? list.items : new CastMember[0]);
            });
        }

        public static IEnumerator GetHistory(int take, Action<Turn[]> done)
        {
            return Get("/api/history?take=" + take, json =>
            {
                var list = JsonUtility.FromJson<TurnList>("{\"items\":" + json + "}");
                done?.Invoke(list != null && list.items != null ? list.items : new Turn[0]);
            });
        }

        /// <summary>
        /// One portrait. The path comes straight out of the cast payload, so the theatre keeps
        /// deciding which of the four candidate drawings this moment calls for -- the client
        /// never works it out on its own.
        /// </summary>
        public static IEnumerator GetPortrait(string path, Action<Texture2D> done)
        {
            if (string.IsNullOrEmpty(path))
            {
                done?.Invoke(null);
                yield break;
            }

            using (var req = UnityWebRequestTexture.GetTexture(BaseUrl + path))
            {
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.Log("Clarry 候选图里没有这张，继续试下一张：" + path);
                    done?.Invoke(null);
                    yield break;
                }

                done?.Invoke(DownloadHandlerTexture.GetContent(req));
            }
        }

        static IEnumerator Get(string path, Action<string> done)
        {
            using (var req = UnityWebRequest.Get(BaseUrl + path))
            {
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("Clarry " + path + " 失败：" + req.error);
                    done?.Invoke("[]");
                    yield break;
                }

                done?.Invoke(req.downloadHandler.text);
            }
        }
    }
}
