using faketuna.spoitable_color_changer.editor;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(SpoitableColorChangerPlugin))]

namespace faketuna.spoitable_color_changer.editor
{
    [RunsOnAllPlatforms]
    internal sealed class SpoitableColorChangerPlugin : Plugin<SpoitableColorChangerPlugin>
    {
        public override string QualifiedName => "faketuna.spoitable-color-changer";
        public override string DisplayName => "Spoitable Color Changer";

        protected override void Configure()
        {
            // MA の Material Swap などでマテリアルが決まった後に色を変える。
            // TexTransTool の前に置くと、TTT → Light Limit Changer → MA の既存の順序と循環するので指定しない
            // (Light Limit Changer があると TTT は MA より前に走るため、結果的にこちらは TTT の後になる)。
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .Run("Recolor textures", RecolorPass.Execute)
                .PreviewingWith(new RecolorPreviewFilter());
        }
    }
}
