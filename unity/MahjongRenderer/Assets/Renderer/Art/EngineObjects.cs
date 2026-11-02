using UnityEngine;

namespace Mahjong.Renderer.Art
{
    public static class EngineObjects
    {
        public static void Release(Object value)
        {
            if (value == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying) { Object.DestroyImmediate(value); return; }
#endif
            Object.Destroy(value);
        }
    }
}
