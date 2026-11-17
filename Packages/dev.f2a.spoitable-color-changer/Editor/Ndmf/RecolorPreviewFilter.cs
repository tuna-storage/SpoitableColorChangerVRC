using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace faketuna.spoitable_color_changer.editor
{
    /// <summary>
    /// NDMF のプレビュー。表示用のマテリアル複製を作り、再配色コアの RenderTexture を差し込む。
    /// RenderTexture は (元テクスチャ, 設定) ごとに共有するので、同じマテリアルを使う Renderer が
    /// 複数あっても描くのは 1 回で済む。
    /// </summary>
    internal sealed class RecolorPreviewFilter : IRenderFilter
    {
        internal static readonly TogglablePreviewNode EnableNode = TogglablePreviewNode.Create(
            () => "Spoitable Color Changer",
            qualifiedName: "faketuna.spoitable-color-changer/Recolor",
            initialState: true
        );

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return EnableNode;
        }

        public bool IsEnabled(ComputeContext context)
        {
            return context.Observe(EnableNode.IsEnabled);
        }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var byRenderer = new Dictionary<Renderer, List<SpoitableColorChanger>>();
            var order = new List<Renderer>();

            void Add(Renderer renderer, SpoitableColorChanger component)
            {
                if (!byRenderer.TryGetValue(renderer, out var list))
                {
                    list = new List<SpoitableColorChanger>();
                    byRenderer[renderer] = list;
                    order.Add(renderer);
                }

                if (!list.Contains(component)) list.Add(component);
            }

            foreach (var root in context.GetAvatarRoots())
            {
                if (!context.ActiveInHierarchy(root)) continue;

                Renderer[] avatarRenderers = null;
                foreach (var component in context.GetComponentsInChildren<SpoitableColorChanger>(root, true))
                {
                    var mode = context.Observe(component, c => c.targetMode);
                    if (mode == TargetMode.Material)
                    {
                        var material = context.Observe(component, c => c.targetMaterial);
                        if (material == null) continue;

                        avatarRenderers ??= context.GetComponentsInChildren<Renderer>(root, true)
                            .Where(TargetResolver.IsSupported)
                            .ToArray();
                        foreach (var renderer in avatarRenderers)
                        {
                            var materials = context.Observe(renderer, r => r.sharedMaterials,
                                (a, b) => a.SequenceEqual(b));
                            if (materials.Contains(material)) Add(renderer, component);
                        }
                    }
                    else
                    {
                        var renderer = context.Observe(component, c => c.targetRenderer);
                        if (renderer == null) renderer = context.GetComponent<Renderer>(component.gameObject);
                        if (renderer == null || !TargetResolver.IsSupported(renderer)) continue;
                        // ビルドと同じく、アバターの外の Renderer は対象にしない
                        if (!renderer.transform.IsChildOf(root.transform)) continue;
                        Add(renderer, component);
                    }
                }
            }

            return order
                .Select(r => RenderGroup.For(r).WithData(byRenderer[r].ToImmutableList(), SameComponents))
                .ToImmutableList();
        }

        private static bool SameComponents(ImmutableList<SpoitableColorChanger> a, ImmutableList<SpoitableColorChanger> b)
        {
            return a.SequenceEqual(b);
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            var components = group.GetData<ImmutableList<SpoitableColorChanger>>();
            var (_, proxy) = proxyPairs.First();

            var node = new Node(components);
            try
            {
                node.Build(proxy, context);
            }
            catch
            {
                // 返さなかったノードは NDMF が Dispose しないので、ここで片付ける
                node.Dispose();
                throw;
            }

            return Task.FromResult<IRenderFilterNode>(node);
        }

        /// <summary>1 枚のテクスチャを再配色する単位。</summary>
        private sealed class Job
        {
            public int Slot;
            public string Property;
            public Texture Source;          // 元のテクスチャ(前のジョブの出力なら null)
            public int SourceJob = -1;      // 同じスロット・プロパティに重ねる場合、前のジョブの番号
            public Material BaseMaterial;   // 複製元のマテリアル
            public int BaseMaterialCrc;     // 複製元のプロパティが変わったら複製し直すため
            public RecolorSettings Settings;
            public Texture Positions;       // モデル上のグラデーションマスクの位置マップ(要らなければ null)
            public Texture AcquiredFrom;    // Target を描いたときの入力
            public RenderTexture Target;    // SharedRenderCache から借りたもの
        }

        private sealed class Node : IRenderFilterNode
        {
            private readonly ImmutableList<SpoitableColorChanger> _components;
            private readonly List<Job> _jobs = new List<Job>();
            private readonly Dictionary<int, Material> _replacements = new Dictionary<int, Material>();
            private readonly HashSet<Material> _ownMaterials = new HashSet<Material>();

            public RenderAspects WhatChanged { get; private set; } = RenderAspects.Material | RenderAspects.Texture;

            public Node(ImmutableList<SpoitableColorChanger> components)
            {
                _components = components;
            }

            /// <summary>現在の設定からジョブを組み立てる。ComputeContext への Observe もここで行う。</summary>
            private List<Job> Plan(Renderer proxy, ComputeContext context)
            {
                var jobs = new List<Job>();
                var materials = proxy.sharedMaterials;

                // マスクエディタで塗るたびに描き直す
                context.Observe(PaintSession.Version);

                foreach (var component in _components)
                {
                    if (component == null) continue;

                    var config = context.Observe(component, ComponentConfig.Capture, ComponentConfig.AreEqual);
                    if (config == null || !config.PreviewEnabled || config.Preset == null) continue;

                    var presetConfig = context.Observe(config.Preset, PresetConfig.Capture, PresetConfig.AreEqual);
                    var settings = RecolorSettings.Create(presetConfig, config);
                    if (settings == null || settings.IsNoop) continue;

                    for (var slot = 0; slot < materials.Length; slot++)
                    {
                        var baseMaterial = BaseMaterialFor(slot, materials, jobs);
                        if (baseMaterial == null || !Targets(config, slot, baseMaterial)) continue;

                        // 元のマテリアルの変更(テクスチャの差し替えや色の変更など)にも追従する
                        var crc = context.Observe(baseMaterial, m => m.ComputeCRC());

                        foreach (var prop in config.TextureProperties)
                        {
                            if (!baseMaterial.HasTexture(prop)) continue;

                            var previous = jobs.FindLastIndex(j => j.Slot == slot && j.Property == prop);
                            var original = baseMaterial.GetTexture(prop);
                            var source = previous >= 0 ? null : original;
                            if (previous < 0 && source == null) continue;

                            // 位置マップは元のテクスチャの UV の並びで求める(重ねがけでも同じ)。キャッシュされるので、
                            // 塗るたびの Refresh でもメッシュの形を焼き直さない
                            Texture positions = null;
                            if (settings.NeedsPositions && original != null)
                            {
                                try
                                {
                                    positions = PositionMap.Get(component, original);
                                }
                                catch (System.Exception e)
                                {
                                    Debug.LogException(e);
                                }
                            }

                            jobs.Add(new Job
                            {
                                Slot = slot,
                                Property = prop,
                                Source = source,
                                SourceJob = previous,
                                BaseMaterial = baseMaterial,
                                BaseMaterialCrc = crc,
                                Settings = settings,
                                Positions = positions,
                            });
                        }
                    }
                }

                return jobs;
            }

            private static bool Targets(ComponentConfig config, int slot, Material baseMaterial)
            {
                if (config.TargetMode == TargetMode.Material)
                {
                    return config.TargetMaterial != null && baseMaterial == config.TargetMaterial;
                }

                return config.MaterialSlot < 0 || config.MaterialSlot == slot;
            }

            private Material BaseMaterialFor(int slot, Material[] proxyMaterials, List<Job> jobs)
            {
                foreach (var j in jobs)
                {
                    if (j.Slot == slot) return j.BaseMaterial;
                }

                var material = proxyMaterials[slot];
                // proxy に自分の複製が残っていた場合は、記録してある複製元を使う
                if (material != null && _ownMaterials.Contains(material))
                {
                    foreach (var j in _jobs)
                    {
                        if (j.Slot == slot) return j.BaseMaterial;
                    }

                    return null;
                }

                return material;
            }

            public void Build(Renderer proxy, ComputeContext context)
            {
                var plan = Plan(proxy, context);

                foreach (var job in plan)
                {
                    if (!_replacements.TryGetValue(job.Slot, out var material))
                    {
                        material = new Material(job.BaseMaterial)
                        {
                            name = job.BaseMaterial.name + " (Spoitable)",
                            hideFlags = HideFlags.DontSave,
                        };
                        _replacements[job.Slot] = material;
                        _ownMaterials.Add(material);
                    }

                    var source = SourceOf(job, _jobs);
                    job.Target = SharedRenderCache.Acquire(source, job.Settings, job.Positions);
                    job.AcquiredFrom = source;
                    _jobs.Add(job);
                    material.SetTexture(job.Property, job.Target);
                }

                WhatChanged = RenderAspects.Material | RenderAspects.Texture;
            }

            private static Texture SourceOf(Job job, List<Job> jobs)
            {
                return job.SourceJob >= 0 ? jobs[job.SourceJob].Target : job.Source;
            }

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> proxyPairs,
                ComputeContext context, RenderAspects updatedAspects)
            {
                // 上流でマテリアルかテクスチャが変わったら、複製し直す
                if ((updatedAspects & (RenderAspects.Material | RenderAspects.Texture)) != 0)
                {
                    return Task.FromResult<IRenderFilterNode>(null);
                }

                var (_, proxy) = proxyPairs.First();
                var plan = Plan(proxy, context);
                if (!SameStructure(plan))
                {
                    return Task.FromResult<IRenderFilterNode>(null);
                }

                var changed = false;
                for (var i = 0; i < _jobs.Count; i++)
                {
                    var job = _jobs[i];
                    var source = SourceOf(job, _jobs); // 前のジョブはこのループで更新済み
                    var settings = plan[i].Settings;
                    var positions = plan[i].Positions;
                    if (job.Settings.Equals(settings) && ReferenceEquals(source, job.AcquiredFrom) &&
                        ReferenceEquals(positions, job.Positions))
                    {
                        continue;
                    }

                    try
                    {
                        var target = SharedRenderCache.Acquire(source, settings, positions);
                        SharedRenderCache.Release(job.Target);
                        job.Target = target;
                        job.AcquiredFrom = source;
                        job.Settings = settings;
                        job.Positions = positions;
                        _replacements[job.Slot].SetTexture(job.Property, target);
                        changed = true;
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                WhatChanged = changed ? RenderAspects.Texture : 0;
                return Task.FromResult<IRenderFilterNode>(this);
            }

            private bool SameStructure(List<Job> plan)
            {
                if (plan.Count != _jobs.Count) return false;
                for (var i = 0; i < plan.Count; i++)
                {
                    var a = plan[i];
                    var b = _jobs[i];
                    if (a.Slot != b.Slot || a.Property != b.Property || a.Source != b.Source ||
                        a.SourceJob != b.SourceJob || a.BaseMaterial != b.BaseMaterial ||
                        a.BaseMaterialCrc != b.BaseMaterialCrc)
                    {
                        return false;
                    }
                }

                return true;
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (proxy == null || _replacements.Count == 0) return;

                var materials = proxy.sharedMaterials;
                var dirty = false;
                foreach (var kv in _replacements)
                {
                    if (kv.Key >= materials.Length || materials[kv.Key] == kv.Value) continue;
                    materials[kv.Key] = kv.Value;
                    dirty = true;
                }

                if (dirty) proxy.sharedMaterials = materials;
            }

            public void Dispose()
            {
                foreach (var job in _jobs) SharedRenderCache.Release(job.Target);
                foreach (var material in _ownMaterials)
                {
                    if (material != null) Object.DestroyImmediate(material);
                }

                _jobs.Clear();
                _ownMaterials.Clear();
                _replacements.Clear();
            }
        }
    }
}
