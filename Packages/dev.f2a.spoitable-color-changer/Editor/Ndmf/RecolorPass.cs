using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// ビルド時の適用。複製されたアバターのマテリアルを複製し、再配色したテクスチャに差し替える。
    /// 元のテクスチャ・マテリアル・プレハブは触らない。生成物の保存は NDMF に任せる。
    /// </summary>
    internal static class RecolorPass
    {
        private readonly struct TextureKey : System.IEquatable<TextureKey>
        {
            private readonly Texture _source;
            private readonly RecolorSettings _settings;
            private readonly Object _positionsOwner; // 位置マップを使うときは、コンポーネントごとに分ける

            public TextureKey(Texture source, RecolorSettings settings, Object positionsOwner)
            {
                _source = source;
                _settings = settings;
                _positionsOwner = positionsOwner;
            }

            public bool Equals(TextureKey other)
            {
                return _source == other._source && _settings.Equals(other._settings) &&
                       _positionsOwner == other._positionsOwner;
            }

            public override bool Equals(object obj)
            {
                return obj is TextureKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return System.HashCode.Combine(_source, _settings);
            }
        }

        public static void Execute(BuildContext context)
        {
            var components = context.AvatarRootObject.GetComponentsInChildren<SpoitableColorChanger>(true);
            if (components.Length == 0) return;

            var root = context.AvatarRootTransform;
            var renderers = TargetResolver.SupportedRenderers(root).ToArray();

            var baked = new Dictionary<TextureKey, Texture2D>();
            // 自分が作った複製 → 元のマテリアル。重ねがけでも「元が何か」で対象を判定するため
            var originalOf = new Dictionary<Material, Material>();
            // (そのスロットの今のマテリアル, コンポーネント) → 複製。
            // 同じマテリアルに同じコンポーネントを当てた結果は Renderer をまたいで共有し、
            // 別のコンポーネントの結果は混ざらない
            var derived = new Dictionary<(Material, SpoitableColorChanger), Material>();

            Material OriginalOf(Material m)
            {
                return originalOf.TryGetValue(m, out var o) ? o : m;
            }

            foreach (var component in components)
            {
                if (component.preset == null)
                {
                    Debug.LogWarning($"[SpoitableColorChanger] {component.name}: プリセットが指定されていません。", component);
                    continue;
                }

                var config = ComponentConfig.Capture(component);
                var settings = RecolorSettings.Create(PresetConfig.Capture(component.preset), config);
                if (settings == null || settings.IsNoop || config.TextureProperties.Length == 0) continue;

                var targets = Targets(component, config, root, renderers, OriginalOf).ToList();
                // 位置マップの元(どの Renderer のどのスロットがどのテクスチャを使っているか)は、マテリアルを
                // 差し替える前に集める(差し替えたあとは複製になっていて見つからない)
                var positionSources = settings.NeedsPositions ? CollectPositionSources(config, targets) : null;
                var positionMaps = new Dictionary<Texture, RenderTexture>();

                try
                {
                foreach (var (renderer, slots) in targets)
                {
                    var materials = renderer.sharedMaterials;
                    var changed = false;

                    foreach (var slot in slots)
                    {
                        var current = materials[slot];
                        if (current == null) continue;

                        var key = (current, component);
                        if (!derived.TryGetValue(key, out var clone))
                        {
                            clone = new Material(current) { name = OriginalOf(current).name };
                            ObjectRegistry.RegisterReplacedObject(current, clone);
                            originalOf[clone] = OriginalOf(current);
                            ApplyTextures(clone, component, config, settings, baked, root, positionSources, positionMaps);
                            derived[key] = clone;
                        }

                        materials[slot] = clone;
                        changed = true;
                    }

                    if (changed) renderer.sharedMaterials = materials;
                }
                }
                finally
                {
                    foreach (var map in positionMaps.Values) PositionMap.Release(map);
                }
            }

            // 役目を終えたので取り除く(VRChat のアップロードでは IEditorOnly として消えるが、
            // Apply on Play などそれ以外の経路でも残さない)
            foreach (var component in components)
            {
                if (component != null) Object.DestroyImmediate(component);
            }
        }

        private static IEnumerable<(Renderer renderer, List<int> slots)> Targets(SpoitableColorChanger component,
            ComponentConfig config, Transform root, Renderer[] renderers, System.Func<Material, Material> originalOf)
        {
            if (config.TargetMode == TargetMode.Material)
            {
                if (config.TargetMaterial == null)
                {
                    Debug.LogWarning($"[SpoitableColorChanger] {component.name}: 対象のマテリアルが指定されていません。", component);
                    yield break;
                }

                foreach (var renderer in renderers)
                {
                    var materials = renderer.sharedMaterials;
                    var slots = new List<int>();
                    for (var i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] != null && originalOf(materials[i]) == config.TargetMaterial) slots.Add(i);
                    }

                    if (slots.Count > 0) yield return (renderer, slots);
                }

                yield break;
            }

            var target = component.ResolveRenderer();
            if (target == null)
            {
                Debug.LogWarning($"[SpoitableColorChanger] {component.name}: 対象の Renderer がありません。", component);
                yield break;
            }

            // アバターの外を指していると、ビルド用の複製ではなくシーン上の実物を書き換えてしまう
            if (!target.transform.IsChildOf(root))
            {
                Debug.LogWarning(
                    $"[SpoitableColorChanger] {component.name}: 対象の Renderer({target.name})がアバターの外にあるので無視します。",
                    component);
                yield break;
            }

            yield return (target, TargetSlots(config.MaterialSlot, target.sharedMaterials.Length).ToList());
        }

        /// <summary>
        /// 対象のスロットのうち、位置マップに使うもの(テクスチャごと)。重ねがけでは前のコンポーネントの結果の
        /// テクスチャがキーになる(ApplyTextures が受け取るのと同じもの)。
        /// </summary>
        private static Dictionary<Texture, List<(Renderer, UvSource)>> CollectPositionSources(ComponentConfig config,
            List<(Renderer renderer, List<int> slots)> targets)
        {
            var result = new Dictionary<Texture, List<(Renderer, UvSource)>>();
            var properties = config.TextureProperties.ToList();
            foreach (var (renderer, slots) in targets)
            {
                var mesh = TargetResolver.MeshOf(renderer);
                var materials = renderer.sharedMaterials;
                foreach (var slot in slots)
                {
                    var material = materials[slot];
                    if (material == null) continue;
                    foreach (var prop in properties)
                    {
                        if (!material.HasTexture(prop)) continue;
                        var texture = material.GetTexture(prop);
                        if (texture == null) continue;
                        var source = TargetResolver.SourceOf(renderer, mesh, material, slot, new List<string> { prop }, texture);
                        if (source == null) continue;
                        if (!result.TryGetValue(texture, out var list))
                        {
                            list = new List<(Renderer, UvSource)>();
                            result[texture] = list;
                        }

                        var item = (renderer, source.Value);
                        if (!list.Contains(item)) list.Add(item);
                    }
                }
            }

            return result;
        }

        private static void ApplyTextures(Material material, SpoitableColorChanger component, ComponentConfig config,
            RecolorSettings settings, Dictionary<TextureKey, Texture2D> baked, Transform root,
            Dictionary<Texture, List<(Renderer, UvSource)>> positionSources, Dictionary<Texture, RenderTexture> positionMaps)
        {
            foreach (var prop in config.TextureProperties)
            {
                if (!material.HasTexture(prop)) continue;
                var source = material.GetTexture(prop);
                if (source == null) continue;

                var key = new TextureKey(source, settings, settings.NeedsPositions ? component : null);
                if (!baked.TryGetValue(key, out var result))
                {
                    try
                    {
                        RenderTexture positions = null;
                        if (positionSources != null && !positionMaps.TryGetValue(source, out positions) &&
                            positionSources.TryGetValue(source, out var sources))
                        {
                            var size = PositionMap.SizeFor(source);
                            positions = PositionMap.Render(root, sources, size.x, size.y);
                            positionMaps[source] = positions;
                        }

                        result = RecolorCore.Bake(source, settings, true, positions);
                    }
                    catch (System.Exception e)
                    {
                        // 1 枚の失敗でほかのテクスチャの処理まで止めない。エラーは NDMF のコンソールに出る
                        ErrorReport.ReportException(e);
                        continue;
                    }

                    ObjectRegistry.RegisterReplacedObject(source, result);
                    baked[key] = result;
                }

                material.SetTexture(prop, result);
            }
        }

        internal static IEnumerable<int> TargetSlots(int materialSlot, int count)
        {
            if (materialSlot < 0)
            {
                for (var i = 0; i < count; i++) yield return i;
            }
            else if (materialSlot < count)
            {
                yield return materialSlot;
            }
        }
    }
}
