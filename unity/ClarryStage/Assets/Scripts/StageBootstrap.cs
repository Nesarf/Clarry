using UnityEngine;

namespace ClarryStage
{
    /// <summary>
    /// The whole stage grows out of code, so the project needs nobody to arrange a scene first:
    /// open it, press Play, and it builds its own canvas. That also keeps this directory free of
    /// binary scene assets, which is the difference between a repository people can read and one
    /// they can only open.
    /// </summary>
    public static class StageBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Object.FindObjectOfType<StageView>() != null) return;

            var go = new GameObject("ClarryStage");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<StageView>();
        }
    }
}
