using System.Collections.Generic;
using nadena.dev.ndmf.runtime;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>コンポーネントがどの Renderer に効くかを求める(ビルドとインスペクタで共有)。</summary>
    internal static class TargetResolver
    {
        public static bool IsSupported(Renderer renderer)
        {
            return renderer is SkinnedMeshRenderer || renderer is MeshRenderer;
        }

        public static IEnumerable<Renderer> SupportedRenderers(Transform root)
        {
            if (root == null) yield break;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (IsSupported(r)) yield return r;
            }
        }

        /// <summary>コンポーネントが属するアバターのルート。アバターの外なら一番上の親。</summary>
        public static Transform AvatarRootOf(Component component)
        {
            var root = RuntimeUtil.FindAvatarInParents(component.transform);
            return root != null ? root : component.transform.root;
        }

        /// <summary>保存済みの設定から、対象のマテリアルを求める。</summary>
        public static List<Material> TargetMaterials(SpoitableColorChanger component)
        {
            var list = new List<Material>();
            if (component == null) return list;

            if (component.targetMode == TargetMode.Material)
            {
                if (component.targetMaterial != null) list.Add(component.targetMaterial);
                return list;
            }

            var renderer = component.ResolveRenderer();
            if (renderer == null) return list;
            var materials = renderer.sharedMaterials;
            foreach (var i in RecolorPass.TargetSlots(component.materialSlot, materials.Length))
            {
                if (materials[i] != null && !list.Contains(materials[i])) list.Add(materials[i]);
            }

            return list;
        }

        /// <summary>対象マテリアルの、指定したプロパティに入っているテクスチャ(重複なし)。</summary>
        public static List<(Material material, string property, Texture texture)> TargetTextures(
            SpoitableColorChanger component)
        {
            var list = new List<(Material, string, Texture)>();
            if (component == null || component.textureProperties == null) return list;

            var seen = new HashSet<Texture>();
            foreach (var material in TargetMaterials(component))
            {
                foreach (var prop in component.textureProperties)
                {
                    if (string.IsNullOrEmpty(prop) || !material.HasTexture(prop)) continue;
                    var texture = material.GetTexture(prop);
                    if (texture != null && seen.Add(texture)) list.Add((material, prop, texture));
                }
            }

            return list;
        }

        /// <summary>Material モードで、アバター内で material を使う Renderer。</summary>
        public static List<Renderer> RenderersUsing(Transform root, Material material)
        {
            var list = new List<Renderer>();
            if (material == null) return list;
            foreach (var r in SupportedRenderers(root))
            {
                foreach (var m in r.sharedMaterials)
                {
                    if (m != material) continue;
                    list.Add(r);
                    break;
                }
            }

            return list;
        }

        public static Mesh MeshOf(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer skinned:
                    return skinned.sharedMesh;
                case MeshRenderer _:
                    var filter = renderer.GetComponent<MeshFilter>();
                    return filter != null ? filter.sharedMesh : null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// texture を表示しているメッシュとサブメッシュ。対象のスロットのうち、マテリアルの
        /// 指定プロパティに texture が入っているものだけ(別のテクスチャのメッシュは混ぜない)。
        /// </summary>
        public static List<UvSource> UvSources(SpoitableColorChanger component, Texture texture)
        {
            var list = new List<UvSource>();
            foreach (var (_, source) in PositionSources(component, texture))
            {
                if (!list.Contains(source)) list.Add(source);
            }

            return list;
        }

        /// <summary>UvSources と同じものを、描いている Renderer と組にして返す(位置マップに使う)。</summary>
        public static List<(Renderer renderer, UvSource source)> PositionSources(SpoitableColorChanger component,
            Texture texture)
        {
            var list = new List<(Renderer, UvSource)>();
            if (component == null || texture == null || component.textureProperties == null) return list;

            var materialMode = component.targetMode == TargetMode.Material;
            List<Renderer> renderers;
            if (materialMode)
            {
                renderers = RenderersUsing(AvatarRootOf(component), component.targetMaterial);
            }
            else
            {
                renderers = new List<Renderer>();
                var r = component.ResolveRenderer();
                if (r != null && IsSupported(r)) renderers.Add(r);
            }

            foreach (var renderer in renderers)
            {
                var mesh = MeshOf(renderer);
                if (mesh == null || mesh.subMeshCount == 0) continue;
                var materials = renderer.sharedMaterials;
                var slots = materialMode
                    ? RecolorPass.TargetSlots(-1, materials.Length)
                    : RecolorPass.TargetSlots(component.materialSlot, materials.Length);
                foreach (var slot in slots)
                {
                    var source = SourceOf(renderer, mesh, materials[slot], slot, component.textureProperties, texture);
                    if (source == null) continue;
                    if (materialMode && materials[slot] != component.targetMaterial) continue;
                    var item = (renderer, source.Value);
                    if (!list.Contains(item)) list.Add(item);
                }
            }

            return list;
        }

        /// <summary>slot のマテリアルが properties のどれかに texture を持っていれば、そのメッシュとサブメッシュ。</summary>
        public static UvSource? SourceOf(Renderer renderer, Mesh mesh, Material material, int slot,
            List<string> properties, Texture texture)
        {
            if (material == null || mesh == null || mesh.subMeshCount == 0) return null;
            var property = PropertyHolding(material, properties, texture);
            if (property == null) return null;
            var scale = material.GetTextureScale(property);
            var offset = material.GetTextureOffset(property);
            // マテリアルの数がサブメッシュより多いと、余ったマテリアルは最後のサブメッシュを描く
            return new UvSource(mesh, Mathf.Min(slot, mesh.subMeshCount - 1),
                new Vector4(scale.x, scale.y, offset.x, offset.y));
        }

        /// <summary>properties のうち、texture が入っている最初のプロパティ。なければ null。</summary>
        private static string PropertyHolding(Material material, List<string> properties, Texture texture)
        {
            foreach (var prop in properties)
            {
                if (!string.IsNullOrEmpty(prop) && material.HasTexture(prop) && material.GetTexture(prop) == texture)
                {
                    return prop;
                }
            }

            return null;
        }
    }
}
