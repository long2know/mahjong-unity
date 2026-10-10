using UnityEngine;

namespace Mahjong.Renderer.Input
{
    public enum PickKind { Entity, Slot, Dice }

    public sealed class PickTarget : MonoBehaviour
    {
        public PickKind Kind { get; private set; }
        public string Id { get; private set; }
        public void Bind(PickKind kind, string id) { Kind = kind; Id = id; }
        public void Clear() { Id = null; }
    }
}
